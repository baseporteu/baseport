using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class StorageEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapStorageEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/files/{bucket}", async (AppDbContext db, HttpContext ctx, string bucket) =>
        {
            var gate = await GateAsync(db, ctx, bucket);
            if (gate.Bucket is not { } declared) return gate.Refusal;
            if (!ctx.Request.HasFormContentType) return Error(ctx, ApiProblem.UnsupportedMediaType, "Send the file as multipart/form-data.");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null) return Error(ctx, ApiProblem.BadRequest, "No file was uploaded.");

            if (FileStore.Check(file, declared.Name, declared.MaxMegabytes * 1024L * 1024) is { } rejection)
                return Error(ctx, ProblemFor(rejection.Reason), rejection.Message);

            var type = ContentTypeFor(file.FileName);
            if (!BucketMethods.AcceptsType(declared, type))
                return Error(ctx, ApiProblem.UnsupportedMediaType, $"This bucket does not accept {type}.");

            var stored = FileStore.Reserve(file, declared.Name);
            await FileStore.WriteAsync(file, stored, ctx.RequestAborted);

            var name = stored[(declared.Name.Length + 1)..];
            return Results.Created($"/api/v1/files/{declared.Name}/{name}", new StoredFileDto(
                stored, declared.Name, name, $"{ctx.Request.Scheme}://{ctx.Request.Host}/uploads/{stored}", file.Length, type));
        }).RequireRateLimiting(RateLimit.Upload);

        app.MapGet("/api/v1/files/{bucket}/{name}", async (AppDbContext db, HttpContext ctx, string bucket, string name) =>
        {
            if (await GateAsync(db, ctx, bucket) is { Bucket: null } refused) return refused.Refusal;

            var path = FileStore.Resolve($"{bucket}/{name}");
            if (path is null || !File.Exists(path)) return Error(ctx, ApiProblem.NotFound, "No such file.");
            return Results.File(path, ContentTypeFor(name), enableRangeProcessing: true);
        });

        app.MapDelete("/api/v1/files/{bucket}/{name}", async (AppDbContext db, HttpContext ctx, string bucket, string name) =>
        {
            if (await GateAsync(db, ctx, bucket) is { Bucket: null } refused) return refused.Refusal;

            var path = FileStore.Resolve($"{bucket}/{name}");
            if (path is null || !File.Exists(path)) return Error(ctx, ApiProblem.NotFound, "No such file.");
            FileStore.Delete($"{bucket}/{name}");
            return Results.Ok(new FileDeletedDto($"{bucket}/{name}"));
        });

        app.MapGet("/api/_admin/buckets", async (AppDbContext db) =>
            Results.Ok((await db.Buckets.OrderBy(b => b.Name).ToListAsync()).Select(BucketDto)));

        app.MapPost("/api/_admin/buckets", async (AppDbContext db, JsonObject body) =>
        {
            var now = DateTime.UtcNow;
            var bucket = new Bucket { Id = Ids.NewShortId(12), CreatedAt = now, UpdatedAt = now };
            Apply(bucket, body);
            if (await ProblemsAsync(db, bucket) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            db.Buckets.Add(bucket);
            await db.SaveChangesAsync();
            return Results.Ok(BucketDto(bucket));
        });

        app.MapPatch("/api/_admin/buckets/{id}", async (AppDbContext db, string id, JsonObject body) =>
        {
            var bucket = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id);
            if (bucket is null) return Results.NotFound();

            Apply(bucket, body);
            if (await ProblemsAsync(db, bucket) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            bucket.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(BucketDto(bucket));
        });

        app.MapDelete("/api/_admin/buckets/{id}", async (AppDbContext db, string id) =>
        {
            var bucket = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id);
            if (bucket is null) return Results.NotFound();

            db.Buckets.Remove(bucket);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = bucket.Id });
        });
    }

    private sealed record Gate(Bucket? Bucket, IResult Refusal);

    private static readonly IResult Open = Results.Empty;

    private static async Task<Gate> GateAsync(AppDbContext db, HttpContext ctx, string name)
    {
        if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller)
            return new(null, Error(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token."));

        var bucket = FileStore.IsBucket(name)
            ? await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Name == name && b.ApiEnabled, ctx.RequestAborted)
            : null;
        if (bucket is null) return new(null, Error(ctx, ApiProblem.NotFound, "No such bucket."));

        if (ApiAuth.ViaJwt(ctx) && !bucket.AllowJwt)
            return new(null, Error(ctx, ApiProblem.Forbidden, "This bucket does not accept end-user tokens."));

        if (!BucketMethods.Allows(bucket, caller, ctx.Request.Method))
        {
            var offered = BucketMethods.Parse(bucket.ApiMethods);
            ctx.Response.Headers.Allow = string.Join(", ", offered);
            return new(null, Error(ctx, ApiProblem.MethodNotAllowed, offered.Contains(ctx.Request.Method.ToUpperInvariant())
                ? "This API key is not enabled for this method."
                : $"{ctx.Request.Method} is not enabled for this bucket."));
        }
        return new(bucket, Open);
    }

    internal static ApiProblem ProblemFor(FileStore.Refusal reason) => reason switch
    {
        FileStore.Refusal.TooLarge => ApiProblem.TooLarge,
        FileStore.Refusal.Type => ApiProblem.UnsupportedMediaType,
        FileStore.Refusal.Full => ApiProblem.InsufficientStorage,
        _ => ApiProblem.BadRequest
    };

    private static void Apply(Bucket bucket, JsonObject body)
    {
        if (body["name"] is JsonValue nv && nv.TryGetValue<string>(out var name)) bucket.Name = name.Trim();
        if (body["description"] is JsonValue dv && dv.TryGetValue<string>(out var description)) bucket.Description = description.Trim();
        if (body["apiEnabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled)) bucket.ApiEnabled = enabled;
        if (body["allowJwt"] is JsonValue jv && jv.TryGetValue<bool>(out var jwt)) bucket.AllowJwt = jwt;
        if (body["maxMegabytes"] is JsonValue mv && mv.TryGetValue<int>(out var max)) bucket.MaxMegabytes = max;
        if (body["contentTypes"] is JsonValue cv && cv.TryGetValue<string>(out var types))
            bucket.ContentTypes = string.Join(",", BucketMethods.ContentTypes(new Bucket { ContentTypes = types }));
        if (body["apiMethods"] is JsonArray methods)
            bucket.ApiMethods = BucketMethods.Serialize(methods.Select(m => m is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""));
    }

    private static readonly System.Text.RegularExpressions.Regex ContentTypePattern =
        new(@"^[a-z0-9][a-z0-9!#$&^_.+-]*/(\*|[a-z0-9][a-z0-9!#$&^_.+-]*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static async Task<List<string>> ProblemsAsync(AppDbContext db, Bucket bucket)
    {
        var errors = new List<string>();
        if (!FileStore.IsBucket(bucket.Name))
            errors.Add("A bucket name is 1 to 32 characters of lower-case letters, digits and hyphens.");
        else if (await db.Buckets.AnyAsync(b => b.Id != bucket.Id && b.Name == bucket.Name))
            errors.Add($"A bucket named '{bucket.Name}' already exists.");

        var ceiling = (int)(FileStore.MaxBytes / 1024 / 1024);
        if (bucket.MaxMegabytes < 1 || bucket.MaxMegabytes > ceiling)
            errors.Add($"The file size limit is 1 to {ceiling} MB.");
        if (bucket.Description.Length > 2000) errors.Add("The description is too long (max 2000 characters).");
        if (BucketMethods.ContentTypes(bucket).FirstOrDefault(t => !ContentTypePattern.IsMatch(t)) is { } bad)
            errors.Add($"'{bad}' is not a content type.");
        if (bucket.ApiEnabled && BucketMethods.Parse(bucket.ApiMethods).Count == 0)
            errors.Add("An enabled bucket needs at least one method.");
        return errors;
    }

    private static object BucketDto(Bucket b) => new
    {
        b.Id,
        b.Name,
        b.Description,
        b.ApiEnabled,
        ApiMethods = BucketMethods.Parse(b.ApiMethods),
        b.AllowJwt,
        b.MaxMegabytes,
        b.ContentTypes,
        b.CreatedAt,
        b.UpdatedAt
    };

    internal static string ContentTypeFor(string name) =>
        ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    private static IResult Error(HttpContext ctx, ApiProblem problem, string message) =>
        ApiProblems.Write(ctx, problem, message);
}
