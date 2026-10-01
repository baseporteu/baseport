using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static partial class SecretStore
{
    public const int MaxValueLength = 8192;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,62}$")]
    private static partial Regex NamePattern();

    public sealed record SecretDto(string Id, string Name, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastUsedAt);

    public static SecretDto Dto(Secret s) => new(s.Id, s.Name, s.CreatedAt, s.UpdatedAt, s.LastUsedAt);

    public static IReadOnlyList<string> NameProblems(string name)
    {
        var errors = new List<string>();
        if (!NamePattern().IsMatch(name)) errors.Add("A secret name is 2 to 63 characters of lower-case letters, digits and hyphens, starting with a letter.");
        return errors;
    }

    public static string? ValueProblem(string? value) =>
        string.IsNullOrEmpty(value) ? "A secret needs a value."
        : value.Length > MaxValueLength ? $"A secret value is at most {MaxValueLength} characters."
        : null;

    public static async Task<string?> ResolveAsync(AppDbContext db, string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (secret is null) return null;
        await db.Secrets.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastUsedAt, DateTime.UtcNow), ct);
        return Secrets.Unprotect(secret.ValueProtected);
    }

    public static async Task<IReadOnlyList<string>> ReferencesAsync(AppDbContext db, string id, CancellationToken ct = default)
    {
        var names = new List<string>();
        foreach (var c in await db.Connections.AsNoTracking().ToListAsync(ct))
            if (c.AuthSecretId == id || Connections.Headers(c).Any(h => h.SecretId == id))
                names.Add($"connection {c.Name}");
        return names;
    }
}
