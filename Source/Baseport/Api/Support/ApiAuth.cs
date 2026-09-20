using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

public static class ApiAuth
{

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static async Task<UserAccount?> ResolveAsync(AppDbContext db, HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var presented = header["Bearer ".Length..].Trim();
        if (presented.Length == 0) return null;

        var account = await ResolveByTokenAsync(db, presented) ?? await ResolveJwtAsync(db, presented);
        if (account is not null) ctx.Items[AdminAuth.ResolvedKey] = account.Id;
        return account;
    }

    public static async Task<UserAccount?> ResolveJwtAsync(AppDbContext db, string token)
    {
        var settings = await db.SettingsAsync();
        if (settings is null || !settings.PublicAuthEnabled) return null;

        var now = DateTime.UtcNow;
        var claims = UserTokens.Verify(token, now);
        return claims is null ? null : await UserTokens.AccountForAsync(db, claims, now);
    }

    public static async Task<UserAccount?> ResolveByTokenAsync(AppDbContext db, string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var hash = HashToken(token);
        var account = await db.UserAccounts.FirstOrDefaultAsync(u => u.ApiTokenHash == hash);
        if (account is null || !account.ApiEnabled || account.IsDisabled) return null;

        if (account.ApiTokenExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return null;
        return account;
    }

    public static async Task<bool> AuthorizeAsync(AppDbContext db, HttpContext ctx) =>
        await ResolveAsync(db, ctx) is not null;
}
