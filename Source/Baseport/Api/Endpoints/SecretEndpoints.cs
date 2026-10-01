using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class SecretEndpoints
{
    public static void MapSecretEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/secrets", async (AppDbContext db) =>
            Results.Ok((await db.Secrets.AsNoTracking().OrderBy(s => s.Name).ToListAsync()).Select(SecretStore.Dto)));

        app.MapPost("/api/_admin/secrets", async (AppDbContext db, JsonObject body) =>
        {
            var name = Text(body, "name").Trim();
            var value = Text(body, "value");
            List<string> errors = [.. SecretStore.NameProblems(name)];
            if (SecretStore.ValueProblem(value) is { } problem) errors.Add(problem);
            if (errors.Count == 0 && await db.Secrets.AnyAsync(s => s.Name == name))
                errors.Add($"A secret named '{name}' already exists.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTime.UtcNow;
            var secret = new Secret { Id = Ids.NewShortId(12), Name = name, ValueProtected = Secrets.Protect(value), CreatedAt = now, UpdatedAt = now };
            db.Secrets.Add(secret);
            await db.SaveChangesAsync();
            return Results.Ok(SecretStore.Dto(secret));
        });

        app.MapPut("/api/_admin/secrets/{id}", async (AppDbContext db, string id, JsonObject body) =>
        {
            var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id);
            if (secret is null) return Results.NotFound();
            var value = Text(body, "value");
            if (SecretStore.ValueProblem(value) is { } problem) return Results.BadRequest(new { errors = new[] { problem } });

            secret.ValueProtected = Secrets.Protect(value);
            secret.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(SecretStore.Dto(secret));
        });

        app.MapDelete("/api/_admin/secrets/{id}", async (AppDbContext db, string id) =>
        {
            var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id);
            if (secret is null) return Results.NotFound();
            if (await SecretStore.ReferencesAsync(db, id) is { Count: > 0 } used)
                return Results.Conflict(new { errors = new[] { $"'{secret.Name}' is used by {string.Join(", ", used)}." } });

            db.Secrets.Remove(secret);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = secret.Id });
        });
    }

    private static string Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
