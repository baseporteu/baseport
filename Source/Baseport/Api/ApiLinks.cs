using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Baseport;

// HATEOAS links and $expand for the public REST API. A link is only written for a destination this API would actually serve, and an expansion is one level deep.
public static class ApiLinks
{
    public const string ExpandParameter = "$expand";

    // A reference field whose target table is published, so it can be linked to and expanded into.
    public sealed record Relation(FieldDefinition Field, TableDefinition Target, List<FieldDefinition> TargetFields);

    public sealed record RecordExtras(JsonObject Links, JsonObject? Expanded);

    public static string Collection(string apiName) => $"/api/v1/{apiName}/records";

    public static string Self(string apiName, string recordId) => $"/api/v1/{apiName}/records/{recordId}";

    // An unpublished target, or one whose GET is switched off, gets no relation at all: a link the API would refuse is worse than no link, and expanding into it would publish a table the author never did.
    public static async Task<List<Relation>> RelationsAsync(AppDbContext db, IEnumerable<FieldDefinition> fields)
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
            .ToListAsync();

        var relations = new List<Relation>();
        foreach (var (field, targetId) in references)
        {
            var target = targets.FirstOrDefault(t => t.Id == targetId);
            if (target is null || !ApiMethods.Allows(target, "GET")) continue;
            relations.Add(new Relation(field, target, target.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList()));
        }
        return relations;
    }

    // A misspelled relation is refused rather than ignored: a client that asked for an embed and silently got none cannot tell that from a null reference.
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

    // Links and embeds for one page of records, computed together so each stored record is parsed once.
    public static async Task<Dictionary<string, RecordExtras>> ForRecordsAsync(
        AppDbContext db,
        string apiName,
        IReadOnlyList<Record> records,
        IReadOnlyList<Relation> relations,
        IReadOnlyList<Relation> expand,
        string? userId)
    {
        var data = records.ToDictionary(r => r.Id, Data);
        var embedded = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var relation in expand)
        {
            var ids = records.Select(r => Reference(data[r.Id], relation.Field.Name)).OfType<string>().Distinct().ToList();
            if (ids.Count == 0) continue;

            var targets = await db.Records.Where(t => t.TableId == relation.Target.Id && ids.Contains(t.Id)).ToListAsync();
            var visible = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var ruled = RecordAccess.HasRule(relation.Target, Permission.Read);
            foreach (var target in targets)
            {
                // An embed must refuse exactly what a direct read of the target would refuse, or $expand becomes a way around the rule.
                if (ruled && !await RecordAccess.AllowsAsync(db, relation.Target, relation.TargetFields, Permission.Read, userId, target.Id)) continue;
                visible[target.Id] = ApiDtos.RecordDto(target, relation.TargetFields);
            }
            embedded[relation.Field.Name] = visible;
        }

        var extras = new Dictionary<string, RecordExtras>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var links = new JsonObject();
            foreach (var relation in relations)
                if (Reference(data[record.Id], relation.Field.Name) is { } targetId)
                    links[relation.Field.Name] = Self(relation.Target.ApiName, targetId);

            // Assigned last so the two navigation links a client always needs cannot be shadowed by a field that happens to be named after one.
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
        AppDbContext db, string apiName, Record record, IReadOnlyList<Relation> relations, IReadOnlyList<Relation> expand, string? userId) =>
        (await ForRecordsAsync(db, apiName, new[] { record }, relations, expand, userId))[record.Id];

    public static JsonObject PageLinks(HttpRequest request, QueryEngine.ListPage page)
    {
        var links = new JsonObject
        {
            ["self"] = Href(request, page.Page),
            ["first"] = Href(request, 1)
        };
        if (page.Page > 1) links["prev"] = Href(request, page.Page - 1);
        if (page.HasMore) links["next"] = Href(request, page.Page + 1);
        // Past QueryEngine.CountCeiling the total is a floor, and a last link computed from it would point at the wrong page.
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
