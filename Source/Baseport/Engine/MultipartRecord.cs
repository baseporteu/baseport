using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

public static class MultipartRecord
{

    public static bool Oversize(HttpContext ctx) =>
        ctx.Items.TryGetValue(OversizeKey, out var flag) && flag is true;

    private const string OversizeKey = "baseport.oversize";

    private const string PendingKey = "baseport.pending-files";

    public static async Task SaveFilesAsync(HttpContext ctx, JsonObject saved)
    {
        if (ctx.Items[PendingKey] is not List<(IFormFile File, string Name)> pending) return;
        ctx.Items.Remove(PendingKey);
        var kept = saved.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToHashSet();
        foreach (var (file, name) in pending)
            if (kept.Contains($"{ctx.Request.Scheme}://{ctx.Request.Host}/uploads/{name}"))
                await FileStore.WriteAsync(file, name, ctx.RequestAborted);
    }

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
        var pending = new List<(IFormFile File, string Name)>();
        ctx.Items[PendingKey] = pending;

        foreach (var f in fields)
        {
            var type = FieldValidation.NormalizeType(f.DataType);
            if (type == "file")
            {
                var file = form.Files[f.Name];
                if (file is null) continue;
                if (file.Length > FileStore.MaxBytes) ctx.Items[OversizeKey] = true;
                if (FileStore.Problem(file) is { } error) { errors.Add($"{f.Name}: {error}"); continue; }
                var stored = FileStore.Reserve(file);
                pending.Add((file, stored));
                obj[f.Name] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/uploads/{stored}";
                continue;
            }

            if (!form.TryGetValue(f.Name, out var values) || values.Count == 0) continue;

            if (type == "multiselect")
                obj[f.Name] = new JsonArray(values.Where(v => !string.IsNullOrEmpty(v)).Select(v => (JsonNode)v!).ToArray());
            else
                obj[f.Name] = RecordEngine.CoerceText(type, values[0] ?? "");
        }
        return (obj, errors);
    }
}
