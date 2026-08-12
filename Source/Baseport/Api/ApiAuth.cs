using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

// Bearer-token auth for the public REST API.
public static class ApiAuth
{
    // What the database stores in place of the token itself.
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Resolves the calling account, or null when the token is absent, unknown, disabled or expired.
    public static async Task<UserAccount?> ResolveAsync(AppDbContext db, HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var presented = header["Bearer ".Length..].Trim();
        if (presented.Length == 0) return null;

        // One indexed lookup, not a scan of every api-enabled account.
        var hash = HashToken(presented);
        var account = await db.UserAccounts.FirstOrDefaultAsync(u => u.ApiTokenHash == hash);
        if (account is null || !account.ApiEnabled || account.IsDisabled) return null;
        // An expired token is refused rather than renewed silently.
        if (account.ApiTokenExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return null;
        return account;
    }

    public static async Task<bool> AuthorizeAsync(AppDbContext db, HttpContext ctx) =>
        await ResolveAsync(db, ctx) is not null;
}
