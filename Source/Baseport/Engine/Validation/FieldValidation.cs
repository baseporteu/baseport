using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baseport;

public static class FieldValidation
{

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private const string PatternProbe = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!";

    public static string? NormalizeType(string? t) => FieldTypes.Find(t)?.Name;

    private static readonly Regex SlugPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly Regex FieldNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static string? SlugSourceField(string optionsJson)
    {
        try
        {
            var o = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson);
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("sourceField", out var sf) &&
                sf.ValueKind == JsonValueKind.String)
            {
                var s = sf.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public static string Slugify(string source)
    {
        var lowered = source.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-", RegexOptions.None, PatternTimeout).Trim('-');
        return slug;
    }

    private static string Str(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : "";

    public const NumberStyles Numeric = NumberStyles.Float;

    public static bool TryNumber(JsonNode? v, out double d)
    {
        d = 0;
        if (v is JsonValue jv)
        {

            if (jv.GetValueKind() == JsonValueKind.Number)
                return jv.TryGetValue(out d) || double.TryParse(jv.ToJsonString(), Numeric, CultureInfo.InvariantCulture, out d);
            if (jv.GetValueKind() == JsonValueKind.String) return double.TryParse(jv.GetValue<string>(), Numeric, CultureInfo.InvariantCulture, out d);
        }
        return false;
    }

    private static bool WithinScale(JsonNode? v, int scale)
    {
        var s = v is JsonValue jv ? jv.ToJsonString() : "";
        var dot = s.IndexOf('.');
        return dot < 0 || s.Length - dot - 1 <= scale;
    }

    private static bool TryInt(JsonNode? v, out int i)
    {
        i = 0;
        if (v is JsonValue jv)
        {
            if (jv.GetValueKind() == JsonValueKind.Number) { i = (int)jv.GetValue<double>(); return true; }
            if (jv.GetValueKind() == JsonValueKind.String) return int.TryParse(jv.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        }
        return false;
    }

    public static List<string> ParseOptions(string optionsJson)
    {
        try
        {
            var o = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(optionsJson) ? "[]" : optionsJson);
            if (o.ValueKind == JsonValueKind.Array)
                return o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return [];
        }
        catch (JsonException) { return []; }
    }

    public static string? RefTableId(string optionsJson)
    {        try
        {
            var o = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson);
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("tableId", out var tid) &&
                tid.ValueKind == JsonValueKind.String)
            {
                var s = tid.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public const int MaxNestingDepth = 3;

    private static readonly JsonSerializerOptions NestedSchemaJson = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<FieldDefinition> NestedFields(string optionsJson)
    {
        try
        {
            var o = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson);
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                return [];
            return fields.Deserialize<List<FieldDefinition>>(NestedSchemaJson) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public static List<string> ValidateFieldValue(FieldDefinition f, JsonNode? v, Func<FieldDefinition, string, bool> recordExists) =>
        ValidateFieldValue(f, v, recordExists, 0);

    private static List<string> ValidateFieldValue(FieldDefinition f, JsonNode? v, Func<FieldDefinition, string, bool> recordExists, int depth)
    {
        var errs = new List<string>();
        var type = FieldTypes.Of(f);
        var t = type.Name;

        bool empty = v is null ||
                     (v is JsonValue jv && (jv.GetValueKind() == JsonValueKind.Null ||
                      (jv.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(jv.GetValue<string>()))));
        if (empty)
        {
            if (f.IsRequired) errs.Add($"{f.Name} is required.");
            return errs;
        }
        if (type.Computed || f.IsHidden) return errs;

        var shapeMatches = type.Shape switch
        {
            FieldShape.Object => v is JsonObject,
            FieldShape.Array => v is JsonArray,
            _ => v is JsonValue
        };
        if (!shapeMatches)
        {
            errs.Add(type.Shape switch
            {
                FieldShape.Object => $"{f.Name} must be a JSON object.",
                FieldShape.Array => $"{f.Name} must be a list of values.",
                _ => $"{f.Name} must be a value, not a nested object or array."
            });
            return errs;
        }

        if (t == "boolean" && f.IsRequired)
        {
            bool notChecked =
                (v is JsonValue bj0 && bj0.GetValueKind() == JsonValueKind.False) ||
                (TryNumber(v, out var bdn) && bdn == 0) ||
                Str(v).ToLowerInvariant() is "false";
            if (notChecked)
            {
                errs.Add($"{f.Name} is required.");
                return errs;
            }
        }

        switch (t)
        {
            case "number":
            case "currency":
                if (!TryNumber(v, out var nv)) errs.Add($"{f.Name} must be a number.");

                else if (f.Min is { } lo && nv < lo) errs.Add($"{f.Name} must be at least {lo.ToString("0.##", CultureInfo.InvariantCulture)}.");
                else if (f.Max is { } hi && nv > hi) errs.Add($"{f.Name} must be at most {hi.ToString("0.##", CultureInfo.InvariantCulture)}.");
                else if (f.Scale is { } sc && !WithinScale(v, sc)) errs.Add($"{f.Name} accepts at most {sc} decimal place{(sc == 1 ? "" : "s")}.");
                break;
            case "boolean":
                if (v is JsonValue bj && (bj.GetValueKind() == JsonValueKind.True || bj.GetValueKind() == JsonValueKind.False)) break;
                if (TryNumber(v, out var bd) && (bd == 0 || bd == 1)) break;
                var bs = Str(v).ToLowerInvariant();
                if (bs is not ("true" or "false")) errs.Add($"{f.Name} must be true or false.");
                break;
            case "date":
                if (!DateTime.TryParse(Str(v), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errs.Add($"{f.Name} must be a valid date.");
                break;
            case "datetime":
                if (!DateTime.TryParse(Str(v), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errs.Add($"{f.Name} must be a valid date/time.");
                break;
            case "select":
                var opts = ParseOptions(f.OptionsJson);
                if (!opts.Contains(Str(v))) errs.Add($"{f.Name} has an invalid selection.");
                break;
            case "multiselect":
                var arr = (JsonArray)v!;
                var mopts = ParseOptions(f.OptionsJson);
                foreach (var item in arr)
                {
                    var s = item is JsonValue ijs && ijs.GetValueKind() == JsonValueKind.String ? ijs.GetValue<string>() : "";
                    if (!mopts.Contains(s)) errs.Add($"{f.Name} has an invalid selection '{s}'.");
                }
                break;
            case "file":
                var fs = Str(v);
                if (!Uri.TryCreate(fs, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                    errs.Add($"{f.Name} must be a valid http(s) URL.");
                break;
            case "reference":
                var tpid = RefTableId(f.OptionsJson);
                if (tpid is null) errs.Add($"{f.Name} has no reference target configured.");
                else if (!recordExists(f, Str(v))) errs.Add($"{f.Name} references a record that doesn't exist.");
                break;
            case "text":
            case "longtext":
            case "richtext":
                var len = Str(v).Length;
                var cap = (int)Math.Min(f.Max ?? (t == "text" ? 255 : 20000), t == "text" ? 255 : 20000);
                if (len > cap) errs.Add($"{f.Name} is too long (max {cap} characters).");
                else if (f.Min is { } minLen && len < minLen) errs.Add($"{f.Name} must be at least {(int)minLen} characters.");
                break;
            case "email":
                if (!AccountValidation.IsEmail(Str(v))) errs.Add($"{f.Name} is not a valid email address.");
                break;
            case "url":
                if (!IsHttpUrl(Str(v))) errs.Add($"{f.Name} must be a valid http(s) URL.");
                break;
            case "time":
                if (!TimeOnly.TryParse(Str(v), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errs.Add($"{f.Name} must be a valid time (HH:mm:ss).");
                break;
            case "slug":
                var slug = Str(v);
                var slugCap = (int)(f.Max ?? 255);
                if (slug.Length > slugCap) errs.Add($"{f.Name} is too long (max {slugCap} characters).");
                else if (!SlugPattern.IsMatch(slug)) errs.Add($"{f.Name} must be lowercase letters, digits and hyphens only.");
                break;
            case "password":
                var pw = Str(v);

                if (pw.StartsWith("pbkdf2$", StringComparison.Ordinal)) break;
                var pwCap = (int)Math.Min(f.Max ?? 128, 128);
                if (pw.Length > pwCap) errs.Add($"{f.Name} is too long (max {pwCap} characters).");
                else if (f.Min is { } pwMin && pw.Length < pwMin) errs.Add($"{f.Name} must be at least {(int)pwMin} characters.");
                break;
            case "json":
                var jobj = (JsonObject)v!;
                var jsonCap = (int)(f.Max ?? 20000);
                if (jobj.ToJsonString().Length > jsonCap) { errs.Add($"{f.Name} is too large (max {jsonCap} characters serialized)."); break; }
                errs.AddRange(ValidateMembers(f, NestedFields(f.OptionsJson), jobj, recordExists, depth));
                break;
            case "array":
                var jarr = (JsonArray)v!;
                var itemCap = (int)(f.Max ?? 1000);
                if (jarr.Count > itemCap) { errs.Add($"{f.Name} must have at most {itemCap} items."); break; }

                var members = NestedFields(f.OptionsJson);
                if (members.Count == 0)
                {
                    if (jarr.Any(item => item is not JsonValue))
                        errs.Add($"{f.Name} must contain only text, number or boolean values, not nested objects or arrays.");
                    break;
                }

                foreach (var row in jarr)
                {
                    if (row is not JsonObject rowObj) { errs.Add($"{f.Name} must contain rows, not bare values."); continue; }
                    errs.AddRange(ValidateMembers(f, members, rowObj, recordExists, depth));
                }
                break;
        }

        if (errs.Count == 0 && !string.IsNullOrWhiteSpace(f.Pattern))
        {
            try
            {
                if (!Regex.IsMatch(Str(v), f.Pattern, RegexOptions.None, PatternTimeout))
                    errs.Add($"{f.Name} does not match the required format.");
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
            {
                errs.Add($"{f.Name} could not be validated.");
            }
        }
        return errs;
    }

    private static List<string> ValidateMembers(
        FieldDefinition owner, IReadOnlyList<FieldDefinition> members, JsonObject obj,
        Func<FieldDefinition, string, bool> recordExists, int depth)
    {
        var errs = new List<string>();
        if (members.Count == 0) return errs;
        if (depth >= MaxNestingDepth)
        {
            errs.Add($"{owner.Name} nests deeper than {MaxNestingDepth} levels.");
            return errs;
        }

        var declared = members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var kv in obj)
            if (!declared.Contains(kv.Key)) errs.Add($"{owner.Name} has an unknown member '{kv.Key}'.");

        foreach (var m in members)
        {
            obj.TryGetPropertyValue(m.Name, out var value);
            errs.AddRange(ValidateFieldValue(m, value, recordExists, depth + 1).Select(e => $"{owner.Name}.{e}"));
        }
        return errs;
    }

    public static List<string> ValidateFieldDefinition(FieldDefinition f, IReadOnlyCollection<string> otherNames, IReadOnlyCollection<string> allNames, Func<string, bool> tableExists) =>
        ValidateFieldDefinition(f, otherNames, allNames, tableExists, 0);

    private static List<string> ValidateFieldDefinition(FieldDefinition f, IReadOnlyCollection<string> otherNames, IReadOnlyCollection<string> allNames, Func<string, bool> tableExists, int depth)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(f.Name)) errs.Add("Field name is required.");
        else if (otherNames.Contains(f.Name)) errs.Add($"Field name '{f.Name}' already exists.");
        else if (f.Name.Length > 64) errs.Add("Field name is too long (max 64 characters).");
        else if (!FieldNamePattern.IsMatch(f.Name))
            errs.Add("Field name must start with a letter or underscore, and contain only letters, digits and underscores.");

        if (f.Label.Length > 128) errs.Add("Field label is too long (max 128 characters).");
        if (f.HelpText.Length > 512) errs.Add("Field help text is too long (max 512 characters).");

        var type = FieldTypes.Find(f.DataType);
        if (type == null) { errs.Add($"Unknown field type '{f.DataType}'."); return errs; }
        var t = type.Name;
        f.DataType = t;

        if (depth > 0 && !type.Nestable) errs.Add($"A {t} field is computed over a whole record and cannot be a member of a nested object.");
        if (depth > 0 && (f.IsUnique || f.IsIdentifier)) errs.Add($"A nested member cannot be unique or a lookup identifier: '{f.Name}'.");

        if (f.Min is { } lo && f.Max is { } hi && lo > hi) errs.Add("Minimum cannot be greater than maximum.");

        if (f.Scale is { } sc)
        {
            if (sc < 0 || sc > 10) errs.Add("Decimal places must be between 0 and 10.");
            if (t != "number" && t != "currency") errs.Add("Only a number or currency field can limit decimal places.");
        }

        if (!string.IsNullOrWhiteSpace(f.ValidationExpr))
        {
            var vr = JsExpr.Validate(f.ValidationExpr, allNames);
            if (!vr.Valid) errs.AddRange(vr.Errors.Select(e => $"Validation rule: {e}"));
        }
        else if (!string.IsNullOrWhiteSpace(f.ValidationMessage))
            errs.Add("A validation message needs a validation rule.");

        if (!string.IsNullOrWhiteSpace(f.Currency))
        {
            f.Currency = f.Currency.Trim().ToUpperInvariant();
            if (t != "currency") errs.Add("Only a currency field can carry a currency code.");
            else if (f.Currency.Length != 3 || !f.Currency.All(char.IsAsciiLetterUpper))
                errs.Add("Currency must be a three-letter ISO 4217 code, for example EUR.");
        }

        if (type.Computed || t == "file")
        {
            if (f.IsIdentifier) errs.Add($"A {t} field cannot be a lookup identifier.");
            if (f.IsRequired && t != "file") errs.Add($"A {t} field is filled in by the server and cannot be required.");
        }
        if ((type.Shape != FieldShape.Scalar || type.Secret) && f.IsIdentifier) errs.Add($"A {t} field cannot be a lookup identifier.");
        if (f.IsIdentifier && f.IsHidden) errs.Add("A hidden field cannot be a lookup identifier.");
        if (f.IsIdentifier && !f.IsRequired && !type.Computed) errs.Add($"Identifier is a match key and must be required: a record stored without a value for '{f.Name}' could never be found by it.");
        if (f.IsUnique && (type.Shape != FieldShape.Scalar || type.Secret || t == "boolean")) errs.Add($"A {t} field cannot be unique.");

        if (!string.IsNullOrWhiteSpace(f.Pattern))
        {
            try
            {

                _ = new Regex(f.Pattern, RegexOptions.None, PatternTimeout).IsMatch(PatternProbe);
            }
            catch (ArgumentException)
            {
                errs.Add("Pattern is not a valid regular expression.");
            }
            catch (RegexMatchTimeoutException)
            {
                errs.Add("Pattern takes too long to evaluate on ordinary input. Simplify it.");
            }
        }

        switch (t)
        {
            case "calculated":
            case "derived":
                var r = JsExpr.Validate(f.Expression, allNames);
                if (!r.Valid) errs.AddRange(r.Errors.Select(e => $"Expression: {e}"));
                if (string.IsNullOrWhiteSpace(f.Expression)) errs.Add($"A JS expression is required for {t} fields.");
                break;
            case "select":
            case "multiselect":
                if (ParseOptions(f.OptionsJson).Count == 0) errs.Add("At least one option is required for this field type.");
                break;
            case "reference":
                var tpid = RefTableId(f.OptionsJson);
                if (tpid is null || !tableExists(tpid)) errs.Add("The reference target table does not exist.");
                break;
            case "slug":
                var srcField = SlugSourceField(f.OptionsJson);
                if (srcField is not null && !otherNames.Contains(srcField))
                    errs.Add($"Slug source field '{srcField}' does not exist.");
                break;
            case "array":
            case "json":
                errs.AddRange(ValidateNestedSchema(f, tableExists, depth));
                break;
        }
        return errs;
    }

    private static List<string> ValidateNestedSchema(FieldDefinition owner, Func<string, bool> tableExists, int depth)
    {
        var errs = new List<string>();
        var members = NestedFields(owner.OptionsJson);
        if (members.Count == 0) return errs;
        if (depth >= MaxNestingDepth)
        {
            errs.Add($"'{owner.Name}' nests deeper than {MaxNestingDepth} levels.");
            return errs;
        }

        var names = members.Select(m => m.Name).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (!seen.Add(m.Name)) errs.Add($"'{owner.Name}' declares '{m.Name}' more than once.");
            errs.AddRange(ValidateFieldDefinition(m, [], names, tableExists, depth + 1).Select(e => $"{owner.Name}: {e}"));
        }
        return errs;
    }

    private static List<string> ValidateButton(JsonElement btn, IReadOnlyCollection<string> fieldNames, string context)
    {
        var errs = new List<string>();
        var action = btn.ValueKind == JsonValueKind.Object && btn.TryGetProperty("action", out var ac) ? ac.GetString() : "submit";
        if (action is not ("submit" or "reset" or "cancel" or "validate" or "link" or "run"))
        {
            errs.Add($"{context}: button action must be one of 'submit', 'reset', 'cancel', 'validate', 'link', 'run'.");
        }
        else if (action == "link")
        {
            var hrefExpr = btn.ValueKind == JsonValueKind.Object && btn.TryGetProperty("hrefExpr", out var he) ? he.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(hrefExpr)) errs.Add($"{context}: link requires a URL expression.");
            else
            {
                var r = JsExpr.Validate(hrefExpr, fieldNames);
                if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"{context} link: {x}"));
            }
        }

        else if (action == "run")
        {
            var expr = btn.ValueKind == JsonValueKind.Object && btn.TryGetProperty("expr", out var ex) ? ex.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(expr)) errs.Add($"{context}: run requires an expression.");
            else
            {
                var r = JsExpr.Validate(expr, fieldNames);
                if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"{context} run: {x}"));
            }
        }
        return errs;
    }

    private static List<string> ValidateShowIf(JsonElement row, int rowIdx, IReadOnlyCollection<string> fieldNames)
    {
        var errs = new List<string>();
        var expr = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("showIf", out var si) ? si.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(expr)) return errs;
        var r = JsExpr.Validate(expr, fieldNames);
        if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"Row {rowIdx + 1} show-if: {x}"));
        return errs;
    }

    private static void ValidateCols(JsonElement row, int rowIdx, IReadOnlyCollection<string> fieldNames, HashSet<string> seen, List<string> errs)
    {
        if (!row.TryGetProperty("cols", out var cols) || cols.ValueKind != JsonValueKind.Array)
        {
            errs.Add($"Row {rowIdx + 1}: missing cols array.");
            return;
        }
        foreach (var col in cols.EnumerateArray())
        {
            if (col.ValueKind != JsonValueKind.Object || !col.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                errs.Add($"Row {rowIdx + 1}: invalid column definition.");
                continue;
            }
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { errs.Add($"Row {rowIdx + 1}: field names must be strings."); continue; }
                var name = item.GetString() ?? "";
                if (!fieldNames.Contains(name)) errs.Add($"Layout references unknown field '{name}'.");
                else if (!seen.Add(name)) errs.Add($"Field '{name}' appears more than once in the layout.");
            }
        }
    }

    public static List<string> ValidateActionDef(ActionDef action, IReadOnlyCollection<FieldDefinition> fields)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(action.Name)) errs.Add("Action name is required.");
        else if (action.Name.Length > 128) errs.Add("Action name is too long (max 128 characters).");

        if (!ActionTriggers.All.Contains(action.TriggerKind))
            errs.Add($"Unknown trigger '{action.TriggerKind}'. Must be one of {string.Join(", ", ActionTriggers.All)}.");

        var fieldNames = fields.Select(f => f.Name).ToList();
        var settableNames = fields.Where(f => !FieldTypes.Of(f).Computed).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        JsonElement steps;
        try { steps = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(action.StepsJson) ? "[]" : action.StepsJson); }
        catch (JsonException) { errs.Add("Steps is not valid JSON."); return errs; }
        if (steps.ValueKind != JsonValueKind.Array) { errs.Add("Steps must be a JSON array."); return errs; }
        if (steps.GetArrayLength() == 0) { errs.Add("An action needs at least one step."); return errs; }

        int i = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var type = step.ValueKind == JsonValueKind.Object && step.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (type == "runExpression")
            {
                var expr = step.TryGetProperty("expr", out var e) ? e.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(expr)) errs.Add($"Step {i + 1}: an expression is required.");
                else
                {
                    var r = JsExpr.Validate(expr, fieldNames);
                    if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"Step {i + 1}: {x}"));
                }
            }
            else if (type == "updateRecord")
            {
                if (!step.TryGetProperty("setJson", out var set) || set.ValueKind != JsonValueKind.Object || !set.EnumerateObject().Any())
                    errs.Add($"Step {i + 1}: at least one field to set is required.");
                else
                    foreach (var prop in set.EnumerateObject())
                    {
                        if (!settableNames.Contains(prop.Name)) { errs.Add($"Step {i + 1}: '{prop.Name}' is not a field on this table, or is server-computed."); continue; }
                        var expr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(expr)) { errs.Add($"Step {i + 1}: '{prop.Name}' needs an expression."); continue; }
                        var r = JsExpr.Validate(expr, fieldNames);
                        if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"Step {i + 1}, '{prop.Name}': {x}"));
                    }
            }
            else if (type == "httpRequest")
            {
                var url = step.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(url)) errs.Add($"Step {i + 1}: a URL is required.");
                else if (ProxyTarget.Problem(url) is { } blocked) errs.Add($"Step {i + 1}: {blocked}");

                var method = (step.TryGetProperty("method", out var m) ? m.GetString() : null) ?? "POST";
                if (!ProxyMethods.Contains(method.ToUpperInvariant()))
                    errs.Add($"Step {i + 1}: method must be one of {string.Join(", ", ProxyMethods)}.");

                if (step.TryGetProperty("headers", out var headers) && headers.ValueKind != JsonValueKind.Object)
                    errs.Add($"Step {i + 1}: headers must be a JSON object.");

                if (step.TryGetProperty("bodyTemplate", out var body))
                {
                    if (body.ValueKind != JsonValueKind.Object) errs.Add($"Step {i + 1}: bodyTemplate must be a JSON object.");
                    else
                        foreach (var prop in body.EnumerateObject())
                        {
                            var expr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(expr)) { errs.Add($"Step {i + 1}: '{prop.Name}' needs an expression."); continue; }
                            var r = JsExpr.Validate(expr, fieldNames);
                            if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"Step {i + 1}, '{prop.Name}': {x}"));
                        }
                }
            }
            else errs.Add($"Step {i + 1}: unknown step type '{type}'.");
            i++;
        }
        return errs;
    }

    public static List<string> ValidateLayout(FormConfig form, IReadOnlyCollection<FieldDefinition> fields)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(form.Title)) errs.Add("Form title is required.");
        else if (form.Title.Length > 128) errs.Add("Form title is too long (max 128 characters).");

        var fieldNames = fields.Select(f => f.Name).ToList();

        JsonElement layout;
        try
        {
            layout = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(form.LayoutJson) ? "[]" : form.LayoutJson);
        }
        catch
        {
            errs.Add("Layout is not valid JSON.");
            return errs;
        }

        JsonElement rows;
        if (layout.ValueKind == JsonValueKind.Object && layout.TryGetProperty("rows", out var rw) && rw.ValueKind == JsonValueKind.Array) rows = rw;
        else if (layout.ValueKind == JsonValueKind.Array) rows = layout;
        else { errs.Add("Layout must be a JSON array or { \"rows\": [...] }."); return errs; }

        int rowIdx = 0;
        var seen = new HashSet<string>();
        foreach (var row in rows.EnumerateArray())
        {
            var t = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("t", out var tp) ? tp.GetString() : null;
            if (t is not ("row" or "group" or "subtotal" or "button" or "container" or "line_items" or "child_table" or "button_bar"))
            {
                errs.Add($"Row {rowIdx + 1}: unknown row type '{t}'.");
                rowIdx++;
                continue;
            }

            errs.AddRange(ValidateShowIf(row, rowIdx, fieldNames));

            if (t is "row" or "group")
            {
                ValidateCols(row, rowIdx, fieldNames, seen, errs);
            }
            else if (t == "subtotal")
            {
                var expr = row.TryGetProperty("expr", out var e) ? e.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(expr)) errs.Add($"Row {rowIdx + 1}: subtotal requires an expression.");
                else
                {
                    var r = JsExpr.Validate(expr, fieldNames);
                    if (!r.Valid) errs.AddRange(r.Errors.Select(x => $"Row {rowIdx + 1} subtotal: {x}"));
                }
                var fmt = row.TryGetProperty("format", out var fm) ? fm.GetString() : "currency";
                if (fmt is not ("plain" or "currency")) errs.Add($"Row {rowIdx + 1}: subtotal format must be 'plain' or 'currency'.");
            }
            else if (t == "button")
            {
                errs.AddRange(ValidateButton(row, fieldNames, $"Row {rowIdx + 1}"));
            }
            else if (t == "container")
            {

                if (!row.TryGetProperty("rows", out var nested) || nested.ValueKind != JsonValueKind.Array || nested.GetArrayLength() == 0)
                {
                    errs.Add($"Row {rowIdx + 1}: a container needs at least one nested row.");
                }
                else
                {
                    int nestedIdx = 0;
                    foreach (var nrow in nested.EnumerateArray())
                    {
                        var nt = nrow.ValueKind == JsonValueKind.Object && nrow.TryGetProperty("t", out var ntp) ? ntp.GetString() : null;
                        if (nt != "row") errs.Add($"Row {rowIdx + 1}, nested row {nestedIdx + 1}: a container may only nest plain rows, not '{nt}'.");
                        else
                        {
                            ValidateCols(nrow, rowIdx, fieldNames, seen, errs);
                            errs.AddRange(ValidateShowIf(nrow, rowIdx, fieldNames));
                        }
                        nestedIdx++;
                    }
                }
            }
            else if (t == "line_items")
            {
                var fieldName = row.TryGetProperty("field", out var fn) ? fn.GetString() ?? "" : "";
                var field = fields.FirstOrDefault(f => f.Name == fieldName);
                if (field is null) errs.Add($"Row {rowIdx + 1}: line items references unknown field '{fieldName}'.");
                else if (NormalizeType(field.DataType) != "array") errs.Add($"Row {rowIdx + 1}: line items must point at an array field.");
                else if (NestedFields(field.OptionsJson).Count == 0) errs.Add($"Row {rowIdx + 1}: '{DisplayName(field)}' has no line-item columns configured.");
                else if (!seen.Add(fieldName)) errs.Add($"Field '{fieldName}' appears more than once in the layout.");
            }
            else if (t == "child_table")
            {

                var childTable = row.TryGetProperty("table", out var ct) ? ct.GetString() ?? "" : "";
                var refField = row.TryGetProperty("refField", out var rfp) ? rfp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(childTable) || string.IsNullOrWhiteSpace(refField))
                    errs.Add($"Row {rowIdx + 1}: child table needs both a table and a reference field chosen.");
            }
            else if (t == "button_bar")
            {
                var align = row.TryGetProperty("align", out var al) ? al.GetString() ?? "flex-end" : "flex-end";
                if (align is not ("flex-start" or "center" or "flex-end" or "space-between"))
                    errs.Add($"Row {rowIdx + 1}: alignment must be one of 'flex-start', 'center', 'flex-end', 'space-between'.");

                if (!row.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array || buttons.GetArrayLength() == 0)
                    errs.Add($"Row {rowIdx + 1}: a button bar needs at least one button.");
                else
                {
                    int btnIdx = 0;
                    foreach (var btn in buttons.EnumerateArray())
                    {
                        errs.AddRange(ValidateButton(btn, fieldNames, $"Row {rowIdx + 1}, button {btnIdx + 1}"));
                        btnIdx++;
                    }
                }
            }
            rowIdx++;
        }
        return errs;
    }

    public static List<string> ValidateTable(TableDefinition table, IReadOnlyCollection<string> existingNames)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(table.Name)) errs.Add("Table name is required.");
        else if (existingNames.Contains(table.Name)) errs.Add($"Table name '{table.Name}' already exists.");
        else if (table.Name.Length > 64) errs.Add("Table name is too long (max 64 characters).");
        if (table.Description.Length > 512) errs.Add("Table description is too long (max 512 characters).");

        if (!string.IsNullOrWhiteSpace(table.ApiName))
        {
            table.ApiName = table.ApiName.Trim().ToLowerInvariant();
            if (!ApiNamePattern.IsMatch(table.ApiName))
                errs.Add("The API name must be 2 to 63 characters: lowercase letters, digits and hyphens, starting with a letter.");
            else if (ReservedApiNames.Contains(table.ApiName))
                errs.Add($"'{table.ApiName}' is reserved and cannot be used as an API name.");
        }
        else if (table.ApiEnabled)
        {
            errs.Add("A table published to the REST API needs an API name.");
        }

        table.ApiDisplayName = table.ApiDisplayName.Trim();
        table.ApiNamespace = table.ApiNamespace.Trim();
        if (table.ApiDisplayName.Length > 64) errs.Add("The documentation name is too long (max 64 characters).");
        if (table.ApiNamespace.Length > 64) errs.Add("The namespace is too long (max 64 characters).");
        if (table.ApiDocumentation.Length > 8000) errs.Add("The documentation is too long (max 8000 characters).");

        table.ApiMethods = ApiMethods.Serialize(ApiMethods.Parse(table.ApiMethods));
        if (table.ApiEnabled && table.ApiMethods.Length == 0)
            errs.Add("A published table needs at least one HTTP method enabled.");

        if (table.IsProxy)
        {
            if (!IsHttpUrl(table.ProxyUrl))
                errs.Add("The proxy target must be an absolute http(s) URL.");
            if (!string.IsNullOrWhiteSpace(table.ProxyReadUrl) && !IsHttpUrl(table.ProxyReadUrl))
                errs.Add("The proxy read endpoint must be an absolute http(s) URL.");
            if (!ProxyMethods.Contains(table.ProxyMethod))
                errs.Add($"The proxy method must be one of {string.Join(", ", ProxyMethods)}.");
        }
        return errs;
    }

    private static readonly string[] ProxyMethods = { "GET", "POST", "PUT", "PATCH", "DELETE" };

    private static readonly Regex ApiNamePattern = new(@"^[a-z][a-z0-9-]{1,62}$", RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedApiNames =
        new(StringComparer.OrdinalIgnoreCase) { "api", "v1", "openapi", "openapi.json", "tables", "forms", "auth", "admin" };

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");

    public static string DisplayName(FieldDefinition f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label;

    public static List<string> ValidateForm(FormConfig form, IReadOnlyCollection<FieldDefinition> fields)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(form.Title)) errs.Add("Form title is required.");
        else if (form.Title.Length > 128) errs.Add("Form title is too long (max 128 characters).");
        if (form.Description.Length > 512) errs.Add("Form description is too long (max 512 characters).");

        var names = fields.Select(f => f.Name).ToList();

        JsonElement config;
        try
        {
            config = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(form.ConfigJson) ? "{}" : form.ConfigJson);
        }
        catch (JsonException)
        {
            errs.Add("Form configuration is not valid JSON.");
            return errs;
        }
        if (config.ValueKind != JsonValueKind.Object)
        {
            errs.Add("Form configuration must be a JSON object.");
            return errs;
        }

        if (form.Kind == FormKinds.List)
        {
            errs.AddRange(ValidateList(config, names));
            return errs;
        }

        var actions = FormActions.Parse(form.Actions);
        if (form.IsReadOnly && actions.Contains(FormActions.Submit))
            errs.Add("A read-only form cannot also submit. Turn off submit, or turn off read-only.");

        if (actions.Contains(FormActions.Submit))
        {
            errs.AddRange(ValidateLayout(form, fields));

            if (LayoutFieldCount(form.LayoutJson) == 0)
                errs.Add("A submit form needs at least one field in its layout.");

            if (config.TryGetProperty("onSuccessRedirect", out var osr) && osr.ValueKind == JsonValueKind.String)
            {
                var expr = osr.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(expr))
                {
                    var vr = JsExpr.Validate(expr, names);
                    if (!vr.Valid) errs.AddRange(vr.Errors.Select(e => $"Redirect on success: {e}"));
                }
            }
        }

        if (actions.Contains(FormActions.Lookup))
            errs.AddRange(ValidateLookup(config, names, fields));

        return errs;
    }

    private static List<string> ValidateLookup(JsonElement config, IReadOnlyCollection<string> names, IReadOnlyCollection<FieldDefinition> fields)
    {
        var errs = new List<string>();
        {
                var match = StringArray(config, "matchFields");
                if (match.Count == 0)
                    errs.Add("A lookup needs at least one identifier field to match on.");
                foreach (var n in match)
                {
                    var f = fields.FirstOrDefault(x => x.Name == n);
                    if (f is null) { errs.Add($"Lookup references unknown field '{n}'."); continue; }

                    if (f.IsHidden) errs.Add($"'{DisplayName(f)}' is hidden and cannot be a lookup identifier.");
                    else if (NormalizeType(f.DataType) is "multiselect" or "file")
                        errs.Add($"'{DisplayName(f)}' is not a usable lookup identifier.");
                }
            errs.AddRange(UnknownNames(config, "resultFields", names, "Lookup result"));
            if (StringArray(config, "resultFields").Count == 0)
                errs.Add("A lookup needs at least one field to show when a record is found.");
        }
        return errs;
    }

    private static List<string> ValidateList(JsonElement config, IReadOnlyCollection<string> names)
    {
        var errs = new List<string>();
        {
                var columns = StringArray(config, "columns");
                if (columns.Count == 0) errs.Add("A list needs at least one column.");
                errs.AddRange(UnknownNames(config, "columns", names, "List column"));
                errs.AddRange(UnknownNames(config, "searchFields", names, "List search field"));

                var sort = config.TryGetProperty("sortField", out var sf) && sf.ValueKind == JsonValueKind.String ? sf.GetString() ?? "" : "";
                if (sort.Length > 0 && !names.Contains(sort)) errs.Add($"List sorts on unknown field '{sort}'.");

                var dir = config.TryGetProperty("sortDir", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() ?? "desc" : "desc";
                if (dir is not ("asc" or "desc")) errs.Add("List sort direction must be 'asc' or 'desc'.");

                if (config.TryGetProperty("filters", out var fl) && fl.ValueKind == JsonValueKind.Array)
                    foreach (var f in fl.EnumerateArray())
                    {
                        var fname = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("field", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "" : "";
                        if (!names.Contains(fname)) { errs.Add($"List filter references unknown field '{fname}'."); continue; }
                        var fop = f.TryGetProperty("op", out var op2) && op2.ValueKind == JsonValueKind.String ? op2.GetString() ?? "eq" : "eq";
                        if (!QueryEngine.FilterOperators.Contains(fop)) errs.Add($"List filter uses unknown operator '{fop}'.");
                    }

                if (config.TryGetProperty("pageSize", out var ps))
                {

                    if (ps.ValueKind != JsonValueKind.Number || !ps.TryGetInt32(out var size) || size < 0 || size > QueryEngine.MaxPageSize)
                        errs.Add($"List page size must be a whole number between 0 and {QueryEngine.MaxPageSize}.");
                }

                if (config.TryGetProperty("renderers", out var rend) && rend.ValueKind == JsonValueKind.Object)
                    foreach (var r in rend.EnumerateObject())
                    {
                        if (!names.Contains(r.Name)) { errs.Add($"List renderer targets unknown column '{r.Name}'."); continue; }
                        if (r.Value.ValueKind != JsonValueKind.String) { errs.Add($"Renderer for '{r.Name}' must be an expression string."); continue; }
                        var expr = r.Value.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(expr)) continue;
                        var vr = JsExpr.Validate(expr, names);
                        if (!vr.Valid) errs.AddRange(vr.Errors.Select(e => $"Renderer for '{r.Name}': {e}"));
                    }

                if (config.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
                    foreach (var a in acts.EnumerateArray())
                    {
                        var label = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(label)) { errs.Add("List action requires a label."); continue; }
                        var hrefExpr = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("hrefExpr", out var he) && he.ValueKind == JsonValueKind.String ? he.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(hrefExpr)) { errs.Add($"Action '{label}' requires a URL expression."); continue; }
                        var vr = JsExpr.Validate(hrefExpr, names);
                        if (!vr.Valid) errs.AddRange(vr.Errors.Select(e => $"Action '{label}': {e}"));
                    }
        }
        return errs;
    }

    private static int LayoutFieldCount(string layoutJson)
    {
        JsonElement layout;
        try { layout = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(layoutJson) ? "[]" : layoutJson); }
        catch (JsonException) { return 0; }

        JsonElement rows;
        if (layout.ValueKind == JsonValueKind.Object && layout.TryGetProperty("rows", out var rw)) rows = rw;
        else rows = layout;
        if (rows.ValueKind != JsonValueKind.Array) return 0;

        return CountRowFields(rows);
    }

    private static int CountRowFields(JsonElement rows)
    {
        var count = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var t = row.TryGetProperty("t", out var tp) ? tp.GetString() : null;

            if (t is "line_items" or "child_table") { count++; continue; }
            if (t == "container")
            {
                if (row.TryGetProperty("rows", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    count += CountRowFields(nested);
                continue;
            }
            if (!row.TryGetProperty("cols", out var cols) || cols.ValueKind != JsonValueKind.Array) continue;
            foreach (var col in cols.EnumerateArray())
                if (col.ValueKind == JsonValueKind.Object && col.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    count += items.EnumerateArray().Count(i => i.ValueKind == JsonValueKind.String);
        }
        return count;
    }

    private static List<string> StringArray(JsonElement config, string property)
    {
        if (!config.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return new List<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static IEnumerable<string> UnknownNames(JsonElement config, string property, IReadOnlyCollection<string> names, string label) =>
        StringArray(config, property)
            .Where(n => !names.Contains(n))
            .Select(n => $"{label} references unknown field '{n}'.");
}

