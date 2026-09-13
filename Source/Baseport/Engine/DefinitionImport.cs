using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Baseport;

public static class DefinitionImport
{

    public const int MaxRows = 5000;
    public const int MaxBytes = 8 * 1024 * 1024;

    private const int SampleRows = 200;

    private const int MaxSelectOptions = 12;

    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly char[] Delimiters = { ',', ';', '\t', '|' };

    public static (List<JsonObject> Rows, string? Error) Parse(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0) return (new(), "The file is empty.");
        if (bytes.Length > MaxBytes) return (new(), $"The file is larger than {MaxBytes / (1024 * 1024)} MB. Split it and import the parts.");

        var text = new UTF8Encoding(false).GetString(bytes).TrimStart('\ufeff');
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        string? error;
        var rows = ext switch
        {
            ".json" => ParseJson(text, out error),
            ".xml" => ParseXml(text, out error),
            _ => ParseDelimited(text, out error)
        };
        if (error != null) return (new(), error);
        if (rows.Count == 0) return (new(), "The file stores no rows to import.");
        if (rows.Count > MaxRows) return (new(), $"The file stores {rows.Count} rows; the limit is {MaxRows} per import.");
        return (rows, null);
    }

    private static List<JsonObject> ParseJson(string text, out string? error)
    {
        error = null;
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (Exception ex) { error = $"The file is not valid JSON: {ex.Message}"; return new(); }

        var records = OpenApiProxy.Records(node);
        if (records.Count == 0) { error = "The JSON stores no array of objects to import."; return new(); }
        return records;
    }

    private const int MaxXmlDepth = 4;

    private static List<JsonObject> ParseXml(string text, out string? error)
    {
        error = null;
        XDocument doc;
        try
        {

            using var reader = System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0
            });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) { error = $"The file is not valid XML: {ex.Message}"; return new(); }
        if (doc.Root is null) { error = "The XML has no root element."; return new(); }

        var candidates = new List<(List<XElement> Els, List<JsonObject> Rows, int Keys)>();
        foreach (var group in doc.Root.DescendantsAndSelf().GroupBy(e => e.Name.LocalName))
        {
            var els = group.ToList();
            var rows = els.Select(Flatten).Where(r => r.Count > 0).ToList();
            if (rows.Count == 0) continue;
            var keys = rows.SelectMany(r => r.Select(kv => kv.Key)).Distinct(StringComparer.Ordinal).Count();
            candidates.Add((els, rows, keys));
        }

        var pick = candidates.OrderByDescending(c => c.Rows.Count * c.Keys).FirstOrDefault();

        while (pick.Rows is not null)
        {
            var inner = candidates.FirstOrDefault(c =>
                !ReferenceEquals(c.Els, pick.Els) &&
                c.Rows.Count == pick.Rows.Count &&
                c.Keys * 2 >= pick.Keys &&
                c.Els.All(e => e.Ancestors().Any(a => pick.Els.Contains(a))));
            if (inner.Rows is null) break;
            pick = inner;
        }

        var best = pick.Rows ?? new List<JsonObject>();

        if (best.Count == 0) error = "The XML stores no elements with values to read as rows.";
        return best;
    }

    private static JsonObject Flatten(XElement el)
    {
        var row = new JsonObject();
        Collect(el, "", row, 0);
        return row;
    }

    private static void Collect(XElement el, string prefix, JsonObject row, int depth)
    {
        foreach (var attr in el.Attributes())
            if (!attr.IsNamespaceDeclaration && !string.IsNullOrWhiteSpace(attr.Value))
                Put(row, prefix + attr.Name.LocalName, attr.Value);

        foreach (var child in el.Elements())
        {
            var key = prefix + child.Name.LocalName;
            if (child.HasElements)
            {
                if (depth < MaxXmlDepth) Collect(child, key + "_", row, depth + 1);
                continue;
            }
            foreach (var attr in child.Attributes())
                if (!attr.IsNamespaceDeclaration && !string.IsNullOrWhiteSpace(attr.Value))
                    Put(row, key + "_" + attr.Name.LocalName, attr.Value);

            if (!string.IsNullOrWhiteSpace(child.Value)) Put(row, key, child.Value.Trim());
        }
    }

    private static void Put(JsonObject row, string key, string value)
    {
        if (!row.TryGetPropertyValue(key, out var existing) || existing is null) { row[key] = JsonValue.Create(value); return; }
        if (existing is JsonArray arr) { arr.Add(JsonValue.Create(value)); return; }
        row[key] = new JsonArray(existing.DeepClone(), JsonValue.Create(value));
    }

    private static List<JsonObject> ParseDelimited(string text, out string? error)
    {
        error = null;
        var delimiter = SniffDelimiter(text);
        var grid = ReadGrid(text, delimiter, MaxRows);
        if (grid.Count < 2) { error = "The file needs a header line and at least one row."; return new(); }

        var header = grid[0];
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Length == 0) name = "column" + (i + 1);

            if (used.TryGetValue(name, out var seen)) { used[name] = seen + 1; name = $"{name}_{seen + 1}"; }
            else used[name] = 1;
            names.Add(name);
        }

        var rows = new List<JsonObject>();
        foreach (var line in grid.Skip(1))
        {
            if (line.Count == 1 && line[0].Trim().Length == 0) continue;
            var row = new JsonObject();
            for (var i = 0; i < names.Count; i++)
                row[names[i]] = JsonValue.Create(i < line.Count ? line[i] : "");
            rows.Add(row);
        }
        return rows;
    }

    private static char SniffDelimiter(string text)
    {
        var best = ',';
        var bestCount = 0;
        foreach (var d in Delimiters)
        {
            var count = 0;
            var quoted = false;
            foreach (var ch in text)
            {
                if (ch == '"') quoted = !quoted;
                else if (!quoted && ch == '\n') break;
                else if (!quoted && ch == d) count++;
            }
            if (count > bestCount) { bestCount = count; best = d; }
        }
        return best;
    }

    private static List<List<string>> ReadGrid(string text, char delimiter, int maxRows)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"') { cell.Append(ch); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }
            if (ch == '"' && cell.Length == 0) { quoted = true; continue; }
            if (ch == delimiter) { row.Add(cell.ToString()); cell.Clear(); continue; }
            if (ch is '\r') continue;
            if (ch is '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);

                if (rows.Count > maxRows + 1) break;
                row = new List<string>();
                continue;
            }
            cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    public static List<OpenApiProxy.FieldProp> InferFields(IReadOnlyList<JsonObject> rows, bool detectChoices = true)
    {
        var props = new List<OpenApiProxy.FieldProp>();
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Take(SampleRows))
            foreach (var kv in row)
                if (seen.Add(kv.Key)) columns.Add(kv.Key);

        foreach (var column in columns)
        {
            var values = new List<JsonNode?>();
            var present = 0;
            foreach (var row in rows.Take(SampleRows))
            {
                if (!row.TryGetPropertyValue(column, out var v)) continue;
                if (IsBlank(v)) continue;
                present++;
                values.Add(v);
            }

            var required = present > 0 && present == Math.Min(rows.Count, SampleRows);
            var prop = Classify(column, values, required, detectChoices);

            if (required && OpenApiProxy.MapFieldType(prop) == "boolean") prop = prop with { Required = false };
            props.Add(prop);
        }
        return props;
    }

    private static OpenApiProxy.FieldProp Classify(string name, List<JsonNode?> values, bool required, bool detectChoices)
    {
        var none = new List<string>();
        if (values.Count == 0) return new OpenApiProxy.FieldProp(name, "string", "", none, false);

        var kinds = values.Select(v => v!.GetValueKind()).ToList();
        if (kinds.All(k => k == System.Text.Json.JsonValueKind.Object)) return new OpenApiProxy.FieldProp(name, "object", "", none, required);
        if (kinds.All(k => k == System.Text.Json.JsonValueKind.Array)) return new OpenApiProxy.FieldProp(name, "array", "", none, required);
        if (kinds.All(k => k is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return new OpenApiProxy.FieldProp(name, "boolean", "", none, required);
        if (kinds.All(k => k == System.Text.Json.JsonValueKind.Number)) return new OpenApiProxy.FieldProp(name, "number", "", none, required);

        var strings = values.Select(AsText).ToList();
        if (strings.All(s => s is "true" or "false" or "True" or "False" or "TRUE" or "FALSE")) return new OpenApiProxy.FieldProp(name, "boolean", "", none, required);

        if (strings.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return new OpenApiProxy.FieldProp(name, "number", "", none, required);
        if (strings.All(IsDate)) return new OpenApiProxy.FieldProp(name, "string", strings.All(s => s.Length <= 10) ? "date" : "date-time", none, required);
        if (strings.All(s => Email.IsMatch(s))) return new OpenApiProxy.FieldProp(name, "string", "email", none, required);
        if (strings.All(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return new OpenApiProxy.FieldProp(name, "string", "uri", none, required);

        var distinct = strings.Distinct(StringComparer.Ordinal).ToList();
        if (detectChoices && distinct.Count > 1 && distinct.Count <= MaxSelectOptions && distinct.Count * 2 <= strings.Count && distinct.All(s => s.Length <= 48))
            return new OpenApiProxy.FieldProp(name, "string", "", distinct.OrderBy(s => s, StringComparer.Ordinal).ToList(), required);

        return new OpenApiProxy.FieldProp(name, "string", "", none, required);
    }

    private static bool IsDate(string s) =>
        s.Length >= 6 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static bool IsBlank(JsonNode? v) =>
        v is null || v.GetValueKind() == System.Text.Json.JsonValueKind.Null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));

    private static string AsText(JsonNode? v) =>
        v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : v?.ToJsonString().Trim('"') ?? "";

    public static string SafeName(string header, int ordinal)
    {
        var sb = new StringBuilder();
        foreach (var ch in header.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        var name = sb.ToString().Trim('_');
        while (name.Contains("__")) name = name.Replace("__", "_");
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]) && name[0] != '_') name = "column" + ordinal;
        return name.Length > 64 ? name[..64] : name;
    }

    public sealed class ColumnMap
    {
        private readonly List<(string Header, FieldDefinition Field)> _pairs;
        public IReadOnlyList<string> MatchedFields { get; }

        private ColumnMap(List<(string, FieldDefinition)> pairs)
        {
            _pairs = pairs;
            MatchedFields = pairs.Select(p => p.Item2.Name).ToList();
        }

        public static ColumnMap For(IReadOnlyList<JsonObject> rows, IReadOnlyList<FieldDefinition> fields)
        {
            var headers = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.Take(SampleRows))
                foreach (var kv in row)
                    if (seen.Add(kv.Key)) headers.Add(kv.Key);

            var pairs = new List<(string, FieldDefinition)>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {

                var header = headers.FirstOrDefault(h =>
                    string.Equals(h, f.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(f.Label) && string.Equals(h, f.Label, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(SafeName(h, 0), f.Name, StringComparison.OrdinalIgnoreCase));
                if (header is null || !taken.Add(header)) continue;
                pairs.Add((header, f));
            }
            return new ColumnMap(pairs);
        }

        public JsonObject Apply(JsonObject row)
        {
            var mapped = new JsonObject();
            foreach (var (header, f) in _pairs)
            {
                if (!row.TryGetPropertyValue(header, out var val) || val is null) continue;

                if (IsBlank(val)) continue;

                mapped[f.Name] = val is JsonValue v && v.TryGetValue<string>(out var text)
                    ? RecordEngine.CoerceText(f.DataType, text)
                    : val.DeepClone();
            }
            return mapped;
        }
    }

    public static JsonObject MapRow(JsonObject row, IReadOnlyList<FieldDefinition> fields) =>
        ColumnMap.For(new[] { row }, fields).Apply(row);

    public sealed record RowError(int Row, List<string> Errors);

    public static async Task<(List<JsonObject> Prepared, List<RowError> Errors)> PrepareRowsAsync(
        AppDbContext db, TableDefinition table, List<FieldDefinition> fields, IReadOnlyList<JsonObject> rows, int maxErrors = 20)
    {
        var prepared = new List<JsonObject>();
        var errors = new List<RowError>();

        var uniques = fields.Where(f => f.IsUnique && !f.IsHidden).ToList();
        var claimed = uniques.ToDictionary(f => f.Name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var map = ColumnMap.For(rows, fields);
        for (var i = 0; i < rows.Count; i++)
        {
            var mapped = map.Apply(rows[i]);
            var outcome = await RecordEngine.PrepareAsync(db, table, fields, mapped);
            var rowErrors = new List<string>(outcome.Errors);

            foreach (var f in uniques)
            {
                if (!mapped.TryGetPropertyValue(f.Name, out var val) || val is null) continue;
                var text = val is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : val.ToJsonString().Trim('"');
                if (!claimed[f.Name].Add(text)) rowErrors.Add($"Field '{f.Name}' must be unique, and '{text}' appears more than once in this file.");
            }

            if (rowErrors.Count > 0)
            {
                if (errors.Count < maxErrors) errors.Add(new RowError(i + 1, rowErrors));
                continue;
            }
            prepared.Add(mapped);
        }
        return (prepared, errors);
    }

    public static List<FieldDefinition> ToFields(IReadOnlyList<OpenApiProxy.FieldProp> props)
    {
        var fields = new List<FieldDefinition>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < props.Count; i++)
        {
            var prop = props[i];
            var name = SafeName(prop.Name, i + 1);

            for (var dupe = 2; !used.Add(name); dupe++) name = $"{SafeName(prop.Name, i + 1)}_{dupe}";
            fields.Add(new FieldDefinition
            {
                Name = name,
                Label = name == prop.Name ? "" : prop.Name,
                DataType = OpenApiProxy.MapFieldType(prop),
                OptionsJson = prop.EnumValues.Count > 0 ? JsonSerializer.Serialize(prop.EnumValues) : "[]",
                IsRequired = prop.Required,
                Position = i
            });
        }
        return fields;
    }
}
