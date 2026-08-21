using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport;

// Public interface schema for external consumers.
public static class ApiDtos
{
    public static long? DatabaseBytes(AppDbContext db)
    {
        var path = db.Database.GetDbConnection().DataSource;
        return path != ":memory:" && File.Exists(path) ? new FileInfo(path).Length : null;
    }

    public static long EstimatedIndexBytes(IEnumerable<TableDefinition> tables, Func<string, int> recordCount) =>
        tables.Sum(t => RecordIndexes.EstimateIndexBytes(t, recordCount(t.Id)));

    public static object TableDto(TableDefinition t, int formCount = 0, int recordCount = 0) => new
    {
        t.Id,
        t.Name,
        t.Description,
        t.IsProxy,
        t.ProxyUrl,
        t.ProxyMethod,
        t.ProxyReadUrl,
        // Whether a token is set, never the token itself.
        HasProxyToken = !string.IsNullOrEmpty(t.ProxyToken),
        t.ApiEnabled,
        t.ApiDocsEnabled,
        t.ApiName,
        t.ApiDisplayName,
        t.ApiNamespace,
        t.ApiDocumentation,
        ApiMethods = Baseport.ApiMethods.Parse(t.ApiMethods),
        t.CreateRule,
        t.ReadRule,
        t.UpdateRule,
        t.DeleteRule,
        t.CreatedAt,
        t.UpdatedAt,
        FormCount = formCount,
        RecordCount = recordCount,
        Fields = t.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).Select(FieldDto)
    };

    public static object FieldDto(FieldDefinition f) => new
    {
        f.Id,
        f.Name,
        f.Label,
        f.HelpText,
        f.DataType,
        f.Expression,
        f.OptionsJson,
        f.Pattern,
        f.DefaultValue,
        f.Currency,
        f.Min,
        f.Max,
        f.Position,
        f.IsRequired,
        f.IsUnique,
        f.IsHidden,
        f.IsIdentifier,
        f.IsReadOnly
    };

    // a password field's hash never leaves the server. Links and expansions are the public API's, so the console passes neither.
    public static JsonObject RecordDto(Record r, IEnumerable<FieldDefinition> fields, JsonObject? links = null, JsonObject? expanded = null)
    {
        var data = (JsonNode.Parse(string.IsNullOrWhiteSpace(r.JsonData) ? "{}" : r.JsonData) as JsonObject) ?? new JsonObject();
        foreach (var f in fields)
            if (FieldTypes.Of(f).Secret) data.Remove(f.Name);

        var dto = new JsonObject
        {
            ["id"] = r.Id,
            ["createdAt"] = JsonValue.Create(r.CreatedAt),
            ["updatedAt"] = JsonValue.Create(r.Modified),
            ["data"] = data
        };
        if (links is not null) dto["links"] = links;
        if (expanded is not null) dto["expanded"] = expanded;
        return dto;
    }

    // Admin-facing form summary. Includes the owning table so the Forms page can stand alone.
    public static object FormDto(FormConfig f, TableDefinition? table = null) => new
    {
        f.Id,
        f.Kind,
        Actions = FormActions.Parse(f.Actions),
        f.IsReadOnly,
        f.Title,
        f.Description,
        f.LayoutJson,
        f.ConfigJson,
        f.IsPublished,
        f.CreatedAt,
        f.UpdatedAt,
        TableId = table?.Id,
        TableName = table?.Name
    };

    // Public form schema handed to the embed.
    public static object PublicFormSchema(FormConfig form, TableDefinition table, IEnumerable<FieldDefinition> visibleFields, string currency = "EUR", string timeZone = "UTC") => new
    {
        // The embed formats subtotals, which belong to no single field, so the instance default travels with the schema.
        Currency = currency,
        // Timestamps go out in UTC. A client that renders one has to be told which zone the instance reads its dates in, or every reader picks their own.
        TimeZone = timeZone,
        Form = new { form.Id, form.Kind, Actions = FormActions.Parse(form.Actions), form.IsReadOnly, form.Title, form.Description, form.LayoutJson, form.ConfigJson },
        Table = new
        {
            table.Id,
            table.Name,
            Fields = visibleFields.OrderBy(f => f.Position).ThenBy(f => f.Id).Select(f => new
            {
                f.Id,
                f.Name,
                f.Label,
                f.HelpText,
                f.DataType,
                f.Expression,
                f.OptionsJson,
                f.Pattern,
                f.DefaultValue,
                f.Currency,
                f.Min,
                f.Max,
                f.IsRequired,
                f.IsReadOnly
            })
        }
    };
}
