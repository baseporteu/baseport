using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ganss.Xss;

namespace Baseport;

// The single write path.
public static class RecordEngine
{
    // shared instance, Sanitize() is safe to call concurrently
    private static readonly HtmlSanitizer Sanitizer = new();

    // A write path verdict: the messages to show a visitor and, alongside them, the storage names of every field that failed, a form renders exactly the offending inputs red instead of asking the visitor to guess.
    public sealed record ValidationOutcome(List<string> Errors, List<string> InvalidFields)
    {
        public bool HasErrors => Errors.Count > 0;
    }

    // Converts text to its target type, or leaves it unchanged for validation to fail.
    public static JsonNode? CoerceText(string? type, string text) => FieldValidation.NormalizeType(type) switch
    {
        "number" or "currency" => double.TryParse(text, FieldValidation.Numeric, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : JsonValue.Create(text),
        "boolean" => text.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" => JsonValue.Create(true),
            "false" or "0" or "off" or "no" => JsonValue.Create(false),
            _ => JsonValue.Create(text)
        },
        "multiselect" => new JsonArray(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => (JsonNode)v).ToArray()),
        "json" or "array" => TryParseNode(text) ?? JsonValue.Create(text),
        _ => JsonValue.Create(text)
    };

    private static JsonNode? TryParseNode(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    // Sanitizes, validates, default-populates, and calculates server-side record values.
    public static async Task<ValidationOutcome> PrepareAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId = null)
    {
        // Unknown keys are dropped instead of rejected: a stale embed sending a removed field should still submit, not hard-fail for the visitor.
        foreach (var kv in obj.ToList())
            if (fields.All(f => f.Name != kv.Key)) obj.Remove(kv.Key);

        ApplyDefaults(fields, obj);
        DeriveSlugs(fields, obj);

        var errors = new List<string>();
        var invalid = new List<string>();
        foreach (var f in fields)
        {
            if (FieldTypes.Of(f).Computed || f.IsHidden) continue;
            obj.TryGetPropertyValue(f.Name, out var val);
            var fieldErrors = FieldValidation.ValidateFieldValue(f, val, (rf, pid) =>
            {
                var tpid = FieldValidation.RefTableId(rf.OptionsJson);
                if (tpid == null) return false;
                var target = db.Tables.FirstOrDefault(t2 => t2.Id == tpid);
                return target != null && db.Records.Any(r => r.TableId == target.Id && r.Id == pid);
            });
            if (fieldErrors.Count > 0)
            {
                errors.AddRange(fieldErrors);
                invalid.Add(f.Name);
            }
        }
        if (errors.Count > 0) return new ValidationOutcome(errors, invalid);

        await CheckUniqueAsync(db, table, fields, obj, excludeRecordId, errors, invalid);
        if (errors.Count > 0) return new ValidationOutcome(errors, invalid);

        SanitizeRichText(fields, obj);
        HashPasswords(fields, obj);

        // System ids are generated server-side only.
        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "systemid"))
            obj[f.Name] = Ids.NewShortId();

        // Recompute calculated fields server-side so stored data stays consistent.
        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "calculated"))
        {
            if (!TryCompute(f, obj, out var err, out var jv))
            { errors.Add($"Field '{f.Name}' has an invalid expression: {err}"); invalid.Add(f.Name); return new ValidationOutcome(errors, invalid); }
            if (jv != null) obj[f.Name] = jv;
        }

        // Derived fields are computed at submit time and always overwrite any client-supplied value; never rendered in forms.
        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "derived"))
        {
            if (!TryCompute(f, obj, out var err, out var jv))
            { errors.Add($"Field '{f.Name}' has an invalid expression: {err}"); invalid.Add(f.Name); return new ValidationOutcome(errors, invalid); }
            if (jv != null) obj[f.Name] = jv;
            else obj.Remove(f.Name);
        }
        return new ValidationOutcome(errors, invalid);
    }

    // Fills in configured defaults for values the caller left out entirely.
    private static void ApplyDefaults(List<FieldDefinition> fields, JsonObject obj)
    {
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f.DefaultValue)) continue;
            if (FieldTypes.Of(f).Computed) continue;

            var present = obj.TryGetPropertyValue(f.Name, out var existing)
                          && existing is not null
                          && !(existing is JsonValue jv && jv.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(jv.GetValue<string>()));
            if (present) continue;

            obj[f.Name] = FieldValidation.NormalizeType(f.DataType) switch
            {
                "number" or "currency" when double.TryParse(f.DefaultValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
                "boolean" => JsonValue.Create(f.DefaultValue.Equals("true", StringComparison.OrdinalIgnoreCase)),
                "multiselect" => ParseDefaultArray(f.DefaultValue),
                _ => JsonValue.Create(f.DefaultValue)
            };
        }
    }

    // runs before validation, a required slug with a source never fails required-ness
    private static void DeriveSlugs(List<FieldDefinition> fields, JsonObject obj)
    {
        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "slug"))
        {
            var has = obj.TryGetPropertyValue(f.Name, out var sv) && sv is JsonValue sjv &&
                      sjv.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(sjv.GetValue<string>());
            if (has) continue;

            var source = FieldValidation.SlugSourceField(f.OptionsJson);
            if (source is null || !obj.TryGetPropertyValue(source, out var srcVal) || srcVal is null) continue;

            var srcText = srcVal is JsonValue sv2 && sv2.GetValueKind() == JsonValueKind.String
                ? sv2.GetValue<string>()
                : srcVal.ToJsonString().Trim('"');
            var slug = FieldValidation.Slugify(srcText);
            if (slug.Length > 0) obj[f.Name] = JsonValue.Create(slug);
        }
    }

    private static void SanitizeRichText(List<FieldDefinition> fields, JsonObject obj)
    {
        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "richtext"))
        {
            if (!obj.TryGetPropertyValue(f.Name, out var rv) || rv is not JsonValue rjv || rjv.GetValueKind() != JsonValueKind.String) continue;
            obj[f.Name] = JsonValue.Create(Sanitizer.Sanitize(rjv.GetValue<string>()));
        }
    }

    // idempotent: pbkdf2$ is not a password anyone typed
    private static void HashPasswords(List<FieldDefinition> fields, JsonObject obj)
    {
        foreach (var f in fields.Where(f => FieldTypes.Of(f).Secret))
        {
            if (!obj.TryGetPropertyValue(f.Name, out var pv) || pv is not JsonValue pjv || pjv.GetValueKind() != JsonValueKind.String) continue;
            var raw = pjv.GetValue<string>();
            if (raw.Length > 0 && !raw.StartsWith("pbkdf2$", StringComparison.Ordinal))
                obj[f.Name] = JsonValue.Create(AdminAuth.HashPassword(raw));
        }
    }

    private static JsonNode ParseDefaultArray(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is JsonArray arr) return arr;
        }
        catch (JsonException) { /* fall through to the single-value form */ }
        return new JsonArray(JsonValue.Create(raw));
    }

    private static object UniqueKey(JsonNode value, string text) =>
        value.GetValueKind() == JsonValueKind.Number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : text;

    // Uniqueness requires instead of a SQL constraint: fields are rows in Fields, not columns, there is no column to constrain.
    // ponytail: check-then-write, so two concurrent writes of one value can both pass. A partial unique index on the generated column would close it, but SchemaBootstrap reconciles every table on start and creating one would then refuse to open a database that already holds duplicates. Add it behind a repair step, never on its own.
    private static async Task CheckUniqueAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId, List<string> errors, List<string> invalid)
    {
        if (table.IsProxy) return; // nothing is stored locally, there is nothing to collide with

        foreach (var f in fields.Where(f => f.IsUnique && !f.IsHidden))
        {
            if (!obj.TryGetPropertyValue(f.Name, out var val) || val is null) continue;
            var text = val is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : val.ToJsonString().Trim('"');
            if (string.IsNullOrWhiteSpace(text)) continue;

            // The indexed generated column turns this from a scan of the table into a seek.
            var column = RecordIndexes.ColumnFor(f) is { } generated
                ? $"r.\"{generated}\""
                : $"json_extract(r.\"JsonData\", '$.\"{f.Name.Replace("'", "''").Replace("\"", "\"\"")}\"')";
            var sql = $$"""
                SELECT EXISTS (
                    SELECT 1 FROM "_records" r
                    WHERE r."TableId" = {0}
                      AND r."Id" <> {1}
                      AND {{column}} = {2} COLLATE NOCASE
                ) AS "Value"
                """;
            var count = await db.Database.SqlQueryRaw<int>(sql, table.Id, excludeRecordId ?? "", UniqueKey(val, text)).SingleAsync();
            if (count > 0)
            {
                errors.Add($"{FieldValidation.DisplayName(f)} must be unique, '{text}' is already used.");
                invalid.Add(f.Name);
            }
        }
    }

    private const int MaxReportedValues = 5;

    public static async Task<List<string>> ConstraintErrorsAsync(AppDbContext db, TableDefinition table, FieldDefinition field, string? storedUnder = null)
    {
        var errors = new List<string>();
        if (table.IsProxy || (!field.IsUnique && !field.IsIdentifier)) return errors;

        var name = string.IsNullOrEmpty(storedUnder) ? field.Name : storedUnder;
        var column = $"json_extract(r.\"JsonData\", '$.\"{name.Replace("'", "''").Replace("\"", "\"\"")}\"')";
        var label = FieldValidation.DisplayName(field);
        var role = field.IsUnique ? "unique" : "a lookup identifier";

        var duplicateSql = $$"""
            SELECT CAST({{column}} AS TEXT) AS "Value"
            FROM "_records" r
            WHERE r."TableId" = {0} AND {{column}} IS NOT NULL AND TRIM(CAST({{column}} AS TEXT)) <> ''
            GROUP BY CAST({{column}} AS TEXT) COLLATE NOCASE
            HAVING COUNT(*) > 1
            ORDER BY COUNT(*) DESC
            LIMIT {{MaxReportedValues + 1}}
            """;
        var duplicates = await db.Database.SqlQueryRaw<string>(duplicateSql, table.Id).ToListAsync();

        if (duplicates.Count > 0)
        {
            var shown = duplicates.Take(MaxReportedValues).Select(v => $"'{v}'");
            var more = duplicates.Count > MaxReportedValues ? ", and others" : "";
            errors.Add($"'{label}' cannot be {role}: {string.Join(", ", shown)}{more} already appear on more than one stored record. Clear the duplicates first.");
        }

        if (!field.IsIdentifier) return errors;

        var missingSql = $$"""
            SELECT COUNT(*) AS "Value" FROM "_records" r
            WHERE r."TableId" = {0} AND ({{column}} IS NULL OR TRIM(CAST({{column}} AS TEXT)) = '')
            """;
        var missing = await db.Database.SqlQueryRaw<int>(missingSql, table.Id).SingleAsync();

        if (missing > 0)
            errors.Add($"'{label}' cannot be a lookup identifier: {missing} stored record(s) carry no value for it, and nothing can ever look those up. Fill them in first.");

        return errors;
    }

    // Merges a partial update onto the stored record, then runs the full write path over the result.
    public static async Task<(JsonObject Merged, ValidationOutcome Outcome)> ApplyUpdateAsync(
        AppDbContext db, TableDefinition table, List<FieldDefinition> fields, Record record, JsonObject patch, bool replace)
    {
        JsonObject merged;
        if (replace)
        {
            merged = patch.DeepClone() as JsonObject ?? new JsonObject();
        }
        else
        {
            merged = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
            foreach (var kv in patch)
            {
                // Member by member, patching does not drop residuals.
                var nested = fields.FirstOrDefault(f => f.Name == kv.Key) is { } f2 && FieldTypes.Of(f2).Shape == FieldShape.Object;
                if (nested && merged[kv.Key] is JsonObject target && kv.Value is JsonObject source) MergeInto(target, source);
                else merged[kv.Key] = kv.Value?.DeepClone();
            }
        }

        // System ids are regenerated on every write, which would hand a record a new identity on each edit.
        var stored = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
        var systemIds = fields
            .Where(f => FieldValidation.NormalizeType(f.DataType) == "systemid")
            .ToDictionary(f => f.Name, f => stored.TryGetPropertyValue(f.Name, out var v) ? v?.DeepClone() : null);

        var outcome = await PrepareAsync(db, table, fields, merged, record.Id);
        if (outcome.HasErrors) return (merged, outcome);

        foreach (var (name, value) in systemIds)
            if (value is not null) merged[name] = value;

        return (merged, outcome);
    }

    // Carries a field's stored values over when it is renamed. The records are keyed by field name, so without this a rename points the field at nothing: the column reads empty on every row and the values it held are orphaned under the old key. RecordIndexes moves the generated column for the same change; this moves the data under it.
    public static async Task<int> RenameFieldDataAsync(AppDbContext db, TableDefinition table, string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return 0;

        var records = await db.Records.Where(r => r.TableId == table.Id).ToListAsync();
        var moved = 0;
        foreach (var record in records)
        {
            var obj = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
            if (!obj.TryGetPropertyValue(from, out var value)) continue;
            obj.Remove(from);
            // The field's own value wins over anything already sitting under the new key, which can only be an orphan of some earlier field.
            obj[to] = value?.DeepClone();
            record.JsonData = obj.ToJsonString();
            moved++;
        }
        if (moved > 0) await db.SaveChangesAsync();
        return moved;
    }

    // Removes a deleted field's values from every record. The console's confirmation says deleting a field "will irreversibly delete all the data contained in this field", and until this ran it did not: the values stayed in the record json, invisible to every read that projects by field but reachable from the SQL console and a backup, stripped a row at a time by PrepareAsync as records happened to be written, and inherited whole by any later field that reused the name. The counterpart of RecordIndexes.DropForAsync.
    public static async Task<int> DropFieldDataAsync(AppDbContext db, TableDefinition table, string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        var records = await db.Records.Where(r => r.TableId == table.Id).ToListAsync();
        var cleared = 0;
        foreach (var record in records)
        {
            var obj = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
            if (!obj.Remove(name)) continue;
            record.JsonData = obj.ToJsonString();
            cleared++;
        }
        if (cleared > 0) await db.SaveChangesAsync();
        return cleared;
    }

    // Brings stored records back in line with the table's computed fields.
    //
    // A computed field is server owned: the server, not the writer, decides its value, so the promise is that every stored record has one. PrepareAsync keeps that promise at write time, which is the only time it was ever kept: adding the field to a table that already stores rows left every one of them empty, and ApplyUpdateAsync then filled them one at a time as records happened to be edited, so the column was silently part-filled instead of plainly empty.
    //
    // This is RecordIndexes.SyncAsync's counterpart. That reconciles the schema objects a field change implies; this reconciles the record data it implies. Both are called from the same places for the same reason, and neither is optional after a field is created or retyped.
    //
    // A system id is filled only where it is missing, never replaced: it is the record's identity, and regenerating it is the bug ApplyUpdateAsync already guards against.
    //
    // `stale` names the fields whose stored values were never this field's to begin with, and for those the identity rule does not apply because there is no identity there to keep. A field retyped into a system id holds leftovers of the old type, and a newly created one holds whatever an earlier field of that name left behind, since deleting a field does not strip its data from the records. Retyping a Y/N column into a system id otherwise left every row reading "Y".
    //
    // A calculated or derived value is recomputed outright either way, because it is a pure function of the record and an edited expression makes every stored value stale in exactly the same sense.
    public static async Task<int> ReconcileComputedAsync(AppDbContext db, TableDefinition table, IReadOnlyCollection<string>? stale = null)
    {
        var computed = table.Fields.Where(f => FieldTypes.Of(f).Computed).ToList();
        if (computed.Count == 0) return 0;

        var staleNames = stale is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(stale, StringComparer.Ordinal);
        var records = await db.Records.Where(r => r.TableId == table.Id).ToListAsync();
        var changed = 0;
        foreach (var record in records)
        {
            var obj = (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();
            var touched = false;

            foreach (var f in computed)
            {
                if (FieldValidation.NormalizeType(f.DataType) == "systemid")
                {
                    if (!IsMissing(obj, f.Name) && !staleNames.Contains(f.Name)) continue;
                    obj[f.Name] = Ids.NewShortId();
                    touched = true;
                    continue;
                }

                // A bad expression is refused when the field is saved, so a failure here is a record the expression cannot read. Leave that one alone instead of failing the whole table.
                if (!TryCompute(f, obj, out _, out var value)) continue;
                var current = obj.TryGetPropertyValue(f.Name, out var existing) ? existing : null;
                if (value is null)
                {
                    // A derived value that computes to nothing is not stored, the same as at write time.
                    if (current is null) continue;
                    obj.Remove(f.Name);
                    touched = true;
                    continue;
                }
                if (current is not null && JsonNode.DeepEquals(current, value)) continue;
                obj[f.Name] = value;
                touched = true;
            }

            if (!touched) continue;
            record.JsonData = obj.ToJsonString();
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync();
        return changed;
    }

    private static bool IsMissing(JsonObject obj, string name) =>
        !obj.TryGetPropertyValue(name, out var v) || v is null ||
        (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(jv.GetValue<string>()));

    // Objects merge, everything else is replaced whole.
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var kv in source)
        {
            if (target[kv.Key] is JsonObject child && kv.Value is JsonObject next) MergeInto(child, next);
            else target[kv.Key] = kv.Value?.DeepClone();
        }
    }

    private static bool TryCompute(FieldDefinition f, JsonObject obj, out string? error, out JsonNode? jv)
    {
        error = null;
        jv = null;
        try
        {
            var val = JsExpr.Evaluate(f.Expression, name => obj.TryGetPropertyValue(name, out var v) ? v : null);
            jv = val switch
            {
                double d when double.IsFinite(d) => JsonValue.Create(Math.Round(d * 100) / 100),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                _ => null
            };
            return true;
        }
        catch (FormatException ex) { error = ex.Message; return false; }
    }
}
