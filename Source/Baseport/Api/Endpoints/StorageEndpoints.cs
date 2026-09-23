using Microsoft.AspNetCore.StaticFiles;

namespace Baseport;

public static class StorageEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapStorageEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/files/{bucket}", async (AppDbContext db, HttpContext ctx, string bucket) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return Error(401, "Missing or invalid bearer token.");
            if (KeyGate(caller, ctx) is { } refused) return refused;
            if (!FileStore.IsBucket(bucket)) return Error(400, "A bucket name is 1 to 32 characters of lower-case letters, digits and hyphens.");
            if (!ctx.Request.HasFormContentType) return Error(400, "Send the file as multipart/form-data.");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null) return Error(400, "No file was uploaded.");

            // ponytail: no disk quota yet
            var (stored, error) = await FileStore.SaveAsync(file, bucket, ctx.RequestAborted);
            if (error is not null) return Error(400, error);

            var name = stored![(bucket.Length + 1)..];
            return Results.Created($"/api/v1/files/{bucket}/{name}", new
            {
                id = stored,
                bucket,
                name,
                url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/uploads/{stored}",
                size = file.Length,
                content_type = ContentTypeFor(name)
            });
        });

        app.MapGet("/api/v1/files/{bucket}/{name}", async (AppDbContext db, HttpContext ctx, string bucket, string name) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return Error(401, "Missing or invalid bearer token.");
            if (KeyGate(caller, ctx) is { } refused) return refused;

            var path = FileStore.Resolve($"{bucket}/{name}");
            if (path is null || !File.Exists(path)) return Error(404, "No such file.");
            return Results.File(path, ContentTypeFor(name), enableRangeProcessing: true);
        });

        app.MapDelete("/api/v1/files/{bucket}/{name}", async (AppDbContext db, HttpContext ctx, string bucket, string name) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return Error(401, "Missing or invalid bearer token.");
            if (KeyGate(caller, ctx) is { } refused) return refused;

            var path = FileStore.Resolve($"{bucket}/{name}");
            if (path is null || !File.Exists(path)) return Error(404, "No such file.");
            File.Delete(path);
            return Results.Ok(new { deleted = $"{bucket}/{name}" });
        });
    }

    private static readonly string[] Methods = ["GET", "POST", "DELETE"];

    internal static IResult? KeyGate(UserAccount caller, HttpContext ctx)
    {
        var granted = ApiMethods.Parse(caller.ApiTokenMethods);
        if (granted.Contains(ctx.Request.Method.ToUpperInvariant())) return null;

        ctx.Response.Headers.Allow = string.Join(", ", Methods.Where(granted.Contains));
        return Error(405, "This API key is not enabled for this method.");
    }

    private static string ContentTypeFor(string name) =>
        ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    private static IResult Error(int status, string message) =>
        Results.Json(new { errors = new[] { message } }, statusCode: status);
}
