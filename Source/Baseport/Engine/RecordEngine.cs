using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ganss.Xss;

namespace Baseport;

public static class RecordEngine
{

    private static readonly HtmlSanitizer Sanitizer = new();

    public sealed record ValidationOutcome(List<string> Errors, List<string> InvalidFields, ValidationFailure Failure = ValidationFailure.None)
    {
        public bool HasErrors => Errors.Count > 0;
    }

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

    public static async Task<ValidationOutcome> PrepareAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId = null)
    {

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
        if (errors.Count > 0) return new ValidationOutcome(errors, invalid, ValidationFailure.Invalid);

        foreach (var f in fields.Where(f => !string.IsNullOrWhiteSpace(f.ValidationExpr)))
        {
            bool ok;
            try { ok = JsExpr.EvaluateBool(f.ValidationExpr, name => obj.TryGetPropertyValue(name, out var v) ? v : null); }
            catch (FormatException) { continue; }
            if (ok) continue;
            errors.Add(string.IsNullOrWhiteSpace(f.ValidationMessage) ? $"{FieldValidation.DisplayName(f)} is not valid." : f.ValidationMessage);
            invalid.Add(f.Name);
        }
        if (errors.Count > 0) return new ValidationOutcome(errors, invalid, ValidationFailure.Invalid);

        await CheckUniqueAsync(db, table, fields, obj, excludeRecordId, errors, invalid);
        if (errors.Count > 0) return new ValidationOutcome(errors, invalid, ValidationFailure.Conflict);

        SanitizeRichText(fields, obj);
        HashPasswords(fields, obj);

        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "systemid"))
            obj[f.Name] = Ids.NewShortId();

        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "calculated"))
        {
            if (!TryCompute(f, obj, out var err, out var jv))
            { errors.Add($"Field '{f.Name}' has an invalid expression: {err}"); invalid.Add(f.Name); return new ValidationOutcome(errors, invalid, ValidationFailure.Invalid); }
            if (jv != null) obj[f.Name] = jv;
        }

        foreach (var f in fields.Where(f => FieldValidation.NormalizeType(f.DataType) == "derived"))
        {
            if (!TryCompute(f, obj, out var err, out var jv))
            { errors.Add($"Field '{f.Name}' has an invalid expression: {err}"); invalid.Add(f.Name); return new ValidationOutcome(errors, invalid, ValidationFailure.Invalid); }
            if (jv != null) obj[f.Name] = jv;
            else obj.Remove(f.Name);
        }
        return new ValidationOutcome(errors, invalid);
    }

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
            return JsonNode.Parse(raw) as JsonArray ?? new JsonArray(JsonValue.Create(raw));
        }
        catch (JsonException) { return new JsonArray(JsonValue.Create(raw)); }
    }

    private static object UniqueKey(JsonNode value, string text) =>
        value.GetValueKind() == JsonValueKind.Number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : text;

    private static async Task CheckUniqueAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId, List<string> errors, List<string> invalid)
    {
        if (table.IsProxy) return;

        foreach (var f in fields.Where(f => f.IsUnique && !f.IsHidden))
        {
            if (!obj.TryGetPropertyValue(f.Name, out var val) || val is null) continue;
            var text = val is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : val.ToJsonString().Trim('"');
            if (string.IsNullOrWhiteSpace(text)) continue;

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

                var nested = fields.FirstOrDefault(f => f.Name == kv.Key) is { } f2 && FieldTypes.Of(f2).Shape == FieldShape.Object;
                if (nested && merged[kv.Key] is JsonObject target && kv.Value is JsonObject source) MergeInto(target, source);
                else merged[kv.Key] = kv.Value?.DeepClone();
            }
        }

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

    public sealed record CompositeSaveOutcome(Record Header, IReadOnlyList<Record> Lines);

    public static async Task<(CompositeSaveOutcome? Result, ValidationOutcome Outcome)> SaveCompositeAsync(
        AppDbContext db,
        TableDefinition header, List<FieldDefinition> headerFields, JsonObject headerData,
        TableDefinition lineTable, List<FieldDefinition> lineFields, IReadOnlyList<JsonObject> lines,
        string refFieldKey, CancellationToken token = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);

        var headerOutcome = await PrepareAsync(db, header, headerFields, headerData);
        if (headerOutcome.HasErrors) return (null, headerOutcome);

        var headerRecord = new Record { TableId = header.Id, Id = Ids.NewShortId(12), JsonData = headerData.ToJsonString(), CreatedAt = DateTime.UtcNow };
        db.Records.Add(headerRecord);

        await db.SaveChangesAsync(token);

        var lineRecords = new List<Record>();
        foreach (var line in lines)
        {
            line[refFieldKey] = headerRecord.Id;
            var lineOutcome = await PrepareAsync(db, lineTable, lineFields, line);
            if (lineOutcome.HasErrors) return (null, lineOutcome);
            var lineRecord = new Record { TableId = lineTable.Id, Id = Ids.NewShortId(12), JsonData = line.ToJsonString(), CreatedAt = DateTime.UtcNow };
            db.Records.Add(lineRecord);
            lineRecords.Add(lineRecord);
        }
        await db.SaveChangesAsync(token);

        await tx.CommitAsync(token);
        return (new CompositeSaveOutcome(headerRecord, lineRecords), new ValidationOutcome(new(), new()));
    }

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

            obj[to] = value?.DeepClone();
            record.JsonData = obj.ToJsonString();
            moved++;
        }
        if (moved > 0) await db.SaveChangesAsync();
        return moved;
    }

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

                if (!TryCompute(f, obj, out _, out var value)) continue;
                var current = obj.TryGetPropertyValue(f.Name, out var existing) ? existing : null;
                if (value is null)
                {

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
