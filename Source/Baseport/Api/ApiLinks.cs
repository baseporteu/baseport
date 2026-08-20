using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Baseport;

public static class ApiLinks
{
    public const string ExpandParameter = "$expand";

    public sealed record Relation(FieldDefinition Field, TableDefinition Target, List<FieldDefinition> TargetFields);

    public sealed record RecordExtras(JsonObject Links, JsonObject? Expanded);

    public static string Collection(string apiName) => $"/api/v1/{apiName}/records";

    public static string Self(string apiName, string recordId) => $"/api/v1/{apiName}/records/{recordId}";

    public static async Task<List<Relation>> RelationsAsync(AppDbContext db, IEnumerable<FieldDefinition> fields, CancellationToken token = default)
    {
        var references = fields
            .Where(f => FieldValidation.NormalizeType(f.DataType) == "reference")
            .Select(f => (Field: f, TargetId: FieldValidation.RefTableId(f.OptionsJson)))
            .Where(x => x.TargetId is not null)
            .ToList();
        if (references.Count == 0) return new List<Relation>();

        var targetIds = references.Select(x => x.TargetId!).Distinct().ToList();
        var targets = await db.Tables.Include(t => t.Fields)
            .Where(t => targetIds.Contains(t.Id) && t.ApiEnabled)
            .ToListAsync(token);

        var relations = new List<Relation>();
        foreach (var (field, targetId) in references)
        {
            var target = targets.FirstOrDefault(t => t.Id == targetId);
            if (target is null || !ApiMethods.Allows(target, "GET")) continue;
            relations.Add(new Relation(field, target, target.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList()));
        }
        return relations;
    }

    public static (List<Relation> Expand, string? Error) ParseExpand(string? expand, IReadOnlyList<Relation> relations)
    {
        var chosen = new List<Relation>();
        foreach (var name in (expand ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
        {
            var relation = relations.FirstOrDefault(r => r.Field.Name == name);
            if (relation is null)
                return (chosen, $"'{name}' is not an expandable reference field.");
            chosen.Add(relation);
        }
        return (chosen, null);
    }

    public static async Task<Dictionary<string, RecordExtras>> ForRecordsAsync(
        AppDbContext db,
        string apiName,
        IReadOnlyList<Record> records,
        IReadOnlyList<Relation> relations,
        IReadOnlyList<Relation> expand,
        string? userId,
        CancellationToken token = default)
    {
        var data = records.ToDictionary(r => r.Id, Data);
        var embedded = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var relation in expand)
        {
            var ids = records.Select(r => Reference(data[r.Id], relation.Field.Name)).OfType<string>().Distinct().ToList();
            if (ids.Count == 0) continue;

            var targets = await ReadableAsync(db, relation, ids, userId, token);
            var visible = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var target in targets) visible[target.Id] = ApiDtos.RecordDto(target, relation.TargetFields);
            embedded[relation.Field.Name] = visible;
        }

        var extras = new Dictionary<string, RecordExtras>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var links = new JsonObject();
            foreach (var relation in relations)
                if (Reference(data[record.Id], relation.Field.Name) is { } targetId)
                    links[relation.Field.Name] = Self(relation.Target.ApiName, targetId);

            links["self"] = Self(apiName, record.Id);
            links["collection"] = Collection(apiName);

            JsonObject? expanded = null;
            foreach (var relation in expand)
            {
                if (Reference(data[record.Id], relation.Field.Name) is not { } targetId) continue;
                if (!embedded.TryGetValue(relation.Field.Name, out var byId) || !byId.TryGetValue(targetId, out var dto)) continue;
                (expanded ??= new JsonObject())[relation.Field.Name] = dto.DeepClone();
            }
            extras[record.Id] = new RecordExtras(links, expanded);
        }
        return extras;
    }

    public static async Task<RecordExtras> ForRecordAsync(
        AppDbContext db, string apiName, Record record, IReadOnlyList<Relation> relations, IReadOnlyList<Relation> expand, string? userId,
        CancellationToken token = default) =>
        (await ForRecordsAsync(db, apiName, new[] { record }, relations, expand, userId, token))[record.Id];

    private static Task<List<Record>> ReadableAsync(AppDbContext db, Relation relation, List<string> ids, string? userId, CancellationToken token)
    {
        var args = new List<object> { relation.Target.Id };
        var where = "r.\"TableId\" = {0}";

        if (RecordAccess.ListClause(relation.Target, relation.TargetFields, "r", userId, args) is { } clause)
            where += $" AND ({clause})";

        var slots = new List<string>();
        foreach (var id in ids)
        {
            slots.Add($"{{{args.Count}}}");
            args.Add(id);
        }
        where += $" AND r.\"Id\" IN ({string.Join(", ", slots)})";

        var sql = $"""
            SELECT r."Id", r."TableId", r."JsonData", r."CreatedAt", r."UpdatedAt"
            FROM "_records" r WHERE {where}
            """;
        return db.Records.FromSqlRaw(sql, args.ToArray()).AsNoTracking().ToListAsync(token);
    }

    public static JsonObject PageLinks(HttpRequest request, QueryEngine.ListPage page)
    {
        var links = new JsonObject
        {
            ["self"] = Href(request, page.Page),
            ["first"] = Href(request, 1)
        };
        if (page.Page > 1) links["prev"] = Href(request, page.Page - 1);
        if (page.HasMore) links["next"] = Href(request, page.Page + 1);
        if (page.CountExact && page.TotalPages > 0) links["last"] = Href(request, page.TotalPages);
        return links;
    }

    private static string Href(HttpRequest request, int page)
    {
        var query = request.Query
            .Where(kv => !string.Equals(kv.Key, "page", StringComparison.OrdinalIgnoreCase))
            .ToList();
        query.Add(new KeyValuePair<string, StringValues>("page", page.ToString(CultureInfo.InvariantCulture)));
        return QueryHelpers.AddQueryString(request.Path.Value ?? "", query);
    }

    private static JsonObject Data(Record record) =>
        (JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject) ?? new JsonObject();

    private static string? Reference(JsonObject data, string fieldName)
    {
        if (data[fieldName] is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.String) return null;
        var id = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}
