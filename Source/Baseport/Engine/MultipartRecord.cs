using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// turns JSON or multipart/form-data into the JsonObject RecordEngine expects, both reach the same one write path
public static class MultipartRecord
{
    public static async Task<(JsonObject Obj, List<string> Errors)> FromRequestAsync(HttpContext ctx, List<FieldDefinition> fields)
    {
        if (!ctx.Request.HasFormContentType)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                var node = doc.RootElement.ValueKind == JsonValueKind.Object ? JsonNode.Parse(doc.RootElement.GetRawText()) : null;
                return (node as JsonObject ?? new JsonObject(), new List<string>());
            }
            catch (JsonException)
            {
                return (new JsonObject(), new List<string> { "Request body is not valid JSON." });
            }
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var obj = new JsonObject();
        var errors = new List<string>();

        foreach (var f in fields)
        {
            var type = FieldValidation.NormalizeType(f.DataType);
            if (type == "file")
            {
                var file = form.Files[f.Name];
                if (file is null) continue; // not part of this submission; existing/default value applies
                var (stored, error) = await FileStore.SaveAsync(file, ctx.RequestAborted);
                if (error is not null) errors.Add($"{f.Name}: {error}");
                else obj[f.Name] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/uploads/{stored}";
                continue;
            }

            if (!form.TryGetValue(f.Name, out var values) || values.Count == 0) continue;

            // A multiselect arrives as one form value per choice; every other type is a single value the write path coerces the same way it coerces an imported one.
            if (type == "multiselect")
                obj[f.Name] = new JsonArray(values.Where(v => !string.IsNullOrEmpty(v)).Select(v => (JsonNode)v!).ToArray());
            else
                obj[f.Name] = RecordEngine.CoerceText(type, values[0] ?? "");
        }
        return (obj, errors);
    }
}
