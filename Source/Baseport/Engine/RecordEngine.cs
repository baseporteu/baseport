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

    // A write path verdict: the messages to show a visitor and, alongside them, the storage names of every field that failed, so a form can paint exactly the offending inputs red instead of asking the visitor to guess.
    public sealed record ValidationOutcome(List<string> Errors, List<string> InvalidFields)
    {
        public bool HasErrors => Errors.Count > 0;
    }

    // Strips unknown keys, applies defaults, validates every field, enforces uniqueness, fills system ids and recomputes calculated/derived values server-side.
    public static async Task<ValidationOutcome> PrepareAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId = null)
    {
        // Unknown keys are dropped rather than rejected: a stale embed sending a removed field should still submit, not hard-fail for the visitor.
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

    // runs before validation, so a required slug with a source never fails required-ness
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

    // Uniqueness lives here rather than in a SQL constraint: fields are rows in Fields, not columns, so there is no column to constrain.
    private static async Task CheckUniqueAsync(AppDbContext db, TableDefinition table, List<FieldDefinition> fields, JsonObject obj, string? excludeRecordId, List<string> errors, List<string> invalid)
    {
        if (table.IsProxy) return; // nothing is stored locally, so there is nothing to collide with

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
                      AND {{column}} = {2}
                ) AS "Value"
                """;
            var count = await db.Database.SqlQueryRaw<int>(sql, table.Id, excludeRecordId ?? "", text).SingleAsync();
            if (count > 0)
            {
                errors.Add($"{FieldValidation.DisplayName(f)} must be unique, '{text}' is already used.");
                invalid.Add(f.Name);
            }
        }
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
                // An object field merges member by member, so patching one member does not drop the rest.
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

    // Objects merge, everything else is replaced whole. A null still writes null, the same as at the top level.
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
