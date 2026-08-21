using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

// Public unguessable IDs (DB uses internal int IDs).
public static class Ids
{
    public static DateTime StartedAt = DateTime.UtcNow;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-";

    public static string NewShortId(int length = 10) => RandomNumberGenerator.GetString(Alphabet, length);
}

// User-defined table stored locally or proxied remotely.
public class TableDefinition
{
    // Permanent unguessable ID.
    public string Id { get; set; } = "";
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = ""; // Shown in table list; tag description in OpenAPI.
    public bool IsProxy { get; set; } = false; // Proxies requests remotely instead of local storage.
    public string ProxyUrl { get; set; } = ""; // Fully-resolved target URL.
    public string ProxyMethod { get; set; } = "POST";
    public string ProxyToken { get; set; } = ""; // Remote Bearer token.
    public string ProxyReadUrl { get; set; } = ""; // GET collection endpoint for lookups/lists.
    public string ProxyQueryJson { get; set; } = "[]"; // Remote GET query params, e.g. ["$filter","$top"].
    public bool ApiEnabled { get; set; } = false; // Exposes the table at /api/v1.

    // whether the table also appears in the OpenAPI document; the route stays live either way
    public bool ApiDocsEnabled { get; set; } = true;

    // Published endpoint name. Required when ApiEnabled is true.
    public string ApiName { get; set; } = "";

    // OpenAPI display name (falls back to ApiName).
    public string ApiDisplayName { get; set; } = "";

    // OpenAPI documentation sidebar group.
    public string ApiNamespace { get; set; } = "";

    // OpenAPI tag description in Markdown (falls back to Description).
    public string ApiDocumentation { get; set; } = "";

    // Comma-separated list of allowed HTTP methods.
    public string ApiMethods { get; set; } = "GET,POST,PATCH,PUT,DELETE";

    // Per-record access rules: a SQLite boolean expression over _USER_, _ROW_ and _REQ_, empty meaning the table is open to every caller the table-level switches already let through. See RecordAccess.
    public string CreateRule { get; set; } = "";
    public string ReadRule { get; set; } = "";
    public string UpdateRule { get; set; } = "";
    public string DeleteRule { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<FieldDefinition> Fields { get; set; } = new();
}

// TableDefinition column definition.
public class FieldDefinition
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Name { get; set; } = string.Empty; // Stable storage key.
    public string Label { get; set; } = ""; // Display name (falls back to Name).
    public string HelpText { get; set; } = ""; // Form input hint.
    public string DataType { get; set; } = "text"; // text|longtext|number|currency|boolean|date|datetime|select|multiselect|file|reference|calculated|derived|systemid|email|phone|url|color|time|rating|slug|richtext|json|array|password
    public string Expression { get; set; } = string.Empty; // JS expression for calculated/derived fields.
    public string OptionsJson { get; set; } = "[]"; // Select options or reference config.
    public string Pattern { get; set; } = string.Empty; // Validation regex.
    public string DefaultValue { get; set; } = ""; // Fallback for omitted fields.
    public string Currency { get; set; } = ""; // ISO 4217 code (falls back to app default).
    public double? Min { get; set; } // Min value or string length.
    public double? Max { get; set; } // Max value or string length.
    public int Position { get; set; } // Display order.
    public bool IsRequired { get; set; } = false;
    public bool IsUnique { get; set; } = false; // Enforced on stored records.
    public bool IsHidden { get; set; } = false; // Internal-only field.
    public bool IsIdentifier { get; set; } = false; // Lookup form match key.
    public bool IsReadOnly { get; set; } = false; // Read-only value output.
}

// Entity rendering layout options.
public static class FormKinds
{
    public const string Form = "form"; // Standard field layout.
    public const string List = "list"; // Paged table view.

    public static string Normalize(string? k) => (k ?? "").ToLowerInvariant() switch
    {
        List => List,
        _ => Form
    };
}

// Permitted visitor interactions for a Form.
public static class FormActions
{
    public const string Submit = "submit"; // Write operations.
    public const string Lookup = "lookup"; // Record searches.

    public static readonly string[] All = { Submit, Lookup };

    // Parses actions, keeping valid items and preserving initial order.
    public static List<string> Parse(string? stored)
    {
        var parsed = (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant())
            .Where(All.Contains)
            .Distinct()
            .ToList();
        return parsed.Count > 0 ? parsed : new List<string> { Submit };
    }

    public static string Serialize(IEnumerable<string> actions) => string.Join(",", actions);
}

// Supported published HTTP methods. GET covers both single-record and list reads.
public static class ApiMethods
{
    public static readonly string[] All = { "GET", "POST", "PATCH", "PUT", "DELETE" };

    // Parses valid upper-case methods from a comma-separated string.
    public static List<string> Parse(string? stored) =>
        (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .Where(All.Contains)
            .Distinct()
            .ToList();

    public static string Serialize(IEnumerable<string> methods) =>
        string.Join(",", methods.Select(m => m.ToUpperInvariant()).Where(All.Contains).Distinct());

    public static bool Allows(TableDefinition table, string method) =>
        Parse(table.ApiMethods).Contains(method.ToUpperInvariant());
}

// Account access levels.
public static class AccountRoles
{
    public const string Admin = "admin"; // Full console access.
    public const string Consumer = "consumer"; // API token access only.
    public const string User = "user"; // Public /auth account. No console, no static token.

    public static readonly string[] All = { Admin, Consumer, User };

    // Normalizes role string; returns null for unknown input.
    public static string? Normalize(string? stored)
    {
        var role = (stored ?? "").Trim().ToLowerInvariant();
        return All.Contains(role) ? role : null;
    }
}

// Form UI configuration bound to a table.
public class FormConfig
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Kind { get; set; } = FormKinds.Form;
    public string Actions { get; set; } = FormActions.Submit; // Ordered comma-separated list. First action is default view.
    public bool IsReadOnly { get; set; } = false; // Disables form inputs and blocks writes.
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = "";
    public string LayoutJson { get; set; } = "[]"; // Submit layout config or legacy row format.
    public string ConfigJson { get; set; } = "{}"; // Kind-specific settings (lookup options, list filters/columns).
    public bool IsPublished { get; set; } = true; // Unpublished forms return 404.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Record
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string JsonData { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UserAccount
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Role { get; set; } = AccountRoles.Consumer;

    // Disabled accounts retain their history but lose login/API access.
    public bool IsDisabled { get; set; } = false;

    // SHA-256 hash of the bearer token.
    public string ApiTokenHash { get; set; } = "";
    public bool ApiEnabled { get; set; } = false;
    public DateTime? ApiTokenExpiresAt { get; set; }
    public string PasswordHash { get; set; } = ""; // PBKDF2-SHA256 hash.
    public bool MustChangePassword { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }

    // A throwaway account with no credential of any kind. It is claimed by setting one, and swept when it is abandoned. Stored rather than inferred from an empty PasswordHash, because an OIDC account and a half-created one have no password either and neither is disposable.
    public bool IsAnonymous { get; set; } = false;

    // The identity provider this account signs in through, and the subject it claims there. The pair is what a sign-in matches on: a username is reassignable at the provider, a subject is not.
    public string OidcProviderId { get; set; } = "";
    public string OidcSubject { get; set; } = "";
}

// A registered OpenID Connect identity provider (Authelia, Authentik, Pocket ID, or anything else that publishes a discovery document).
public class OidcProvider
{
    public string Id { get; set; } = "";

    // Url-safe key that appears in the callback path, so the redirect URI registered at the provider survives a rename of the display label.
    public string Slug { get; set; } = "";
    public string Name { get; set; } = ""; // Button label on the sign-in screens.
    public string Authority { get; set; } = ""; // Issuer URL; discovery is read from {Authority}/.well-known/openid-configuration.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = ""; // Write-only: never returned, exposed as HasClientSecret.
    public string Scopes { get; set; } = "openid profile email";

    // Which claim carries the username and which the email. Authelia and Authentik send preferred_username; Pocket ID does too.
    public string UsernameClaim { get; set; } = "preferred_username";
    public string EmailClaim { get; set; } = "email";

    public bool IsEnabled { get; set; } = false;
    public bool ConsoleEnabled { get; set; } = false; // Offered at /_/auth.
    public bool PublicEnabled { get; set; } = false; // Offered at /auth/login.

    // Off means an unknown subject is refused rather than provisioned: the instance is then no more open than its account list.
    public bool CreateAccounts { get; set; } = false;

    public int Position { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UserSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string RefreshTokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class SavedQuery
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastExecutedAt { get; set; }

    // A saved query that carries a cron expression is the operator's own scheduled task, run by the same scheduler as the maintenance jobs. Empty is a query somebody runs by hand.
    public string Schedule { get; set; } = "";

    // Paused rather than unscheduled: the cron survives so the operator does not have to write it again.
    public bool ScheduleEnabled { get; set; } = false;

    // Where the result is posted after a scheduled run. Empty records the row count on the query instead, which is a report an operator reads in the console.
    public string WebhookUrl { get; set; } = "";

    public DateTime? NextRunAt { get; set; }
    public string LastResult { get; set; } = "";
}

// Global application settings.
public class AppSettings
{
    public int Id { get; set; } = 1;
    public string AppName { get; set; } = "Baseport";
    public string SiteUrl { get; set; } = "";
    public int LogRetentionSec { get; set; } = 604800;
    public string Currency { get; set; } = "EUR"; // Fallback currency code.

    // IANA zone the console and the published schema render timestamps in. Storage stays UTC; this is a display default, defined once so every client agrees on it. A new instance starts on the host's zone, an upgraded one keeps the UTC its migration wrote.
    public string TimeZone { get; set; } = TimeZones.HostDefault;
    public int BackupRetention { get; set; } = 5; // Maximum local backups to retain.

    // Preview link signing secret (generated on first boot).
    public string PreviewSecret { get; set; } = "";

    // Public end-user auth at /auth and /api/auth/v1. Off by default: it is a second account surface.
    public bool PublicAuthEnabled { get; set; } = false;

    // Self sign-up at /auth/register. Off until an operator opens it; an admin can still create user accounts from the console.
    public bool PublicRegistrationEnabled { get; set; } = false;

    // Throwaway accounts at /api/auth/v1/anonymous, so a visitor can carry data before deciding to sign up. Off by default: it lets an unauthenticated caller create rows in _users.
    public bool AnonymousAuthEnabled { get; set; } = false;

    // Abandoned anonymous accounts are swept after this many days. Zero keeps them forever. Rows they created stay: a record is owned by a value in its own json, not by a foreign key, so nothing here can find them.
    public int AnonymousRetentionDays { get; set; } = 30;

    // iss and aud on issued JWTs. Changing it invalidates every token already handed out.
    public string AuthIssuer { get; set; } = "baseport";

    public int AuthTokenLifetimeSec { get; set; } = 3600;
    public int AuthRefreshLifetimeDays { get; set; } = 30;

    // Allowed embedding origins (one per line; empty permits all).
    public string AllowedOrigins { get; set; } = "";

    // false 404s /api/openapi.json entirely; /api/v1 routes are unaffected either way
    public bool OpenApiEnabled { get; set; } = true;

    // OpenAPI specification title.
    public string ApiTitle { get; set; } = "REST API";

    // OpenAPI specification description (Markdown).
    public string ApiDescription { get; set; } =
        "A secure REST API for managing your resources. All endpoints are under `/api/v1/` and require a bearer token.";

    // Outbound proxy fetches otherwise reach anything the server can reach, including cloud metadata and the console's own port. Off means only public addresses; on says the server's own network is a legitimate target.
    public bool ProxyPrivateTargetsEnabled { get; set; } = false;

    // Postgres wire-protocol listener: off by default, it's a second authentication surface.
    public bool PostgresEnabled { get; set; } = false;
    public int PostgresPort { get; set; } = 5432;
    public string PostgresBindAddress { get; set; } = "127.0.0.1";

    // TDS (SQL Server) wire-protocol listener: off by default, it's a second authentication surface.
    public bool TdsEnabled { get; set; } = false;
    public int TdsPort { get; set; } = 1433;
    public string TdsBindAddress { get; set; } = "127.0.0.1";
}

public class AuditLog
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    // User ID associated with the operation (empty for anonymous).
    public string UserId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public string TableName { get; set; } = "";
    public string Message { get; set; } = "";
}

// Scheduled system job configuration. Uses static keys from an internal registry.
public class JobConfig
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Schedule { get; set; } = ""; // Cron expression (5 or 6 fields).
    public bool Enabled { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string LastResult { get; set; } = "";
}