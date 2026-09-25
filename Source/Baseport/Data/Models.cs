using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Baseport;

public static class Ids
{
    public static DateTime StartedAt = DateTime.UtcNow;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-";

    public static string NewShortId(int length = 10) => RandomNumberGenerator.GetString(Alphabet, length);
}

public class TableDefinition
{

    public string Id { get; set; } = "";
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = "";
    public bool IsProxy { get; set; } = false;
    public string ProxyUrl { get; set; } = "";
    public string ProxyMethod { get; set; } = "POST";
    public string ProxyToken { get; set; } = "";
    public string ProxyReadUrl { get; set; } = "";
    public string ProxyQueryJson { get; set; } = "[]";
    public bool ApiEnabled { get; set; } = false;

    public bool ApiDocsEnabled { get; set; } = true;

    public string ApiName { get; set; } = "";

    public string ApiDisplayName { get; set; } = "";

    public string ApiNamespace { get; set; } = "";

    public string ApiDocumentation { get; set; } = "";

    public string ApiMethods { get; set; } = "GET,POST,PATCH,PUT,DELETE";

    public string CreateRule { get; set; } = "";
    public string ReadRule { get; set; } = "";
    public string UpdateRule { get; set; } = "";
    public string DeleteRule { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<FieldDefinition> Fields { get; set; } = new();
}

public class FieldDefinition
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = "";
    public string HelpText { get; set; } = "";
    public string DataType { get; set; } = "text";
    public string Expression { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string Pattern { get; set; } = string.Empty;
    public string ValidationExpr { get; set; } = string.Empty;
    public string ValidationMessage { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = "";
    public string Currency { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public int? Scale { get; set; }
    public int Position { get; set; }
    public bool IsRequired { get; set; } = false;
    public bool IsUnique { get; set; } = false;
    public bool IsHidden { get; set; } = false;
    public bool IsIdentifier { get; set; } = false;
    public bool IsReadOnly { get; set; } = false;
}

public static class FormKinds
{
    public const string Form = "form";
    public const string List = "list";

    public static string Normalize(string? k) => (k ?? "").ToLowerInvariant() switch
    {
        List => List,
        _ => Form
    };
}

public static class FormActions
{
    public const string Submit = "submit";
    public const string Lookup = "lookup";

    public static readonly string[] All = { Submit, Lookup };

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

public static class ApiMethods
{
    public static readonly string[] All = { "GET", "POST", "PATCH", "PUT", "DELETE" };

    public static List<string> Parse(string? stored) =>
        (stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .Where(All.Contains)
            .Distinct()
            .ToList();

    public static string Serialize(IEnumerable<string> methods) =>
        string.Join(",", methods.Select(m => m.ToUpperInvariant()).Where(All.Contains).Distinct());

    public const string Default = "GET,POST,PATCH,PUT,DELETE";

    public static bool Allows(TableDefinition table, string method) =>
        Parse(table.ApiMethods).Contains(method.ToUpperInvariant());

    // Table ApiMethods AND caller ApiTokenMethods; either narrows a verb independently.
    public static bool Allows(TableDefinition table, UserAccount caller, string method)
    {
        var m = method.ToUpperInvariant();
        return Parse(table.ApiMethods).Contains(m) && Parse(caller.ApiTokenMethods).Contains(m);
    }
}

public static class AccountRoles
{
    public const string Admin = "admin";
    public const string Consumer = "consumer";
    public const string User = "user";

    public static readonly string[] All = { Admin, Consumer, User };

    public static string? Normalize(string? stored)
    {
        var role = (stored ?? "").Trim().ToLowerInvariant();
        return All.Contains(role) ? role : null;
    }
}

public class FormConfig
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Kind { get; set; } = FormKinds.Form;
    public string Actions { get; set; } = FormActions.Submit;
    public bool IsReadOnly { get; set; } = false;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = "";
    public string LayoutJson { get; set; } = "[]";
    public string ConfigJson { get; set; } = "{}";
    public bool IsPublished { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ActionDef
{
    public string Id { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Name { get; set; } = string.Empty;
    public string TriggerKind { get; set; } = ActionTriggers.OnCreate;
    public string StepsJson { get; set; } = "[]";
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ActionTriggers
{
    public const string OnCreate = "onCreate";
    public const string OnUpdate = "onUpdate";
    public const string OnDelete = "onDelete";

    public static readonly string[] All = { OnCreate, OnUpdate, OnDelete };

    public static string? ForRecordAction(string action) => action switch
    {
        "create" => OnCreate,
        "update" => OnUpdate,
        "delete" => OnDelete,
        _ => null
    };
}

public class PendingActionRun
{
    public string Id { get; set; } = "";
    public string ActionDefId { get; set; } = "";
    public string TableId { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string TriggerKind { get; set; } = ActionTriggers.OnCreate;
    public string Status { get; set; } = ActionRunStatus.Pending;
    public int Attempts { get; set; } = 0;
    public DateTime NextAttemptAt { get; set; }
    public string LastError { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ActionRunStatus
{
    public const string Pending = "pending";
    public const string Done = "done";

    public const string Failed = "failed";
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

    public bool IsDisabled { get; set; } = false;

    public string ApiTokenHash { get; set; } = "";
    public bool ApiEnabled { get; set; } = false;
    public DateTime? ApiTokenExpiresAt { get; set; }
    public string ApiTokenMethods { get; set; } = ApiMethods.Default;
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }

    public bool IsAnonymous { get; set; } = false;

    public string OidcProviderId { get; set; } = "";
    public string OidcSubject { get; set; } = "";
}

public class OidcProvider
{
    public string Id { get; set; } = "";

    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";

    public string UsernameClaim { get; set; } = "preferred_username";
    public string EmailClaim { get; set; } = "email";

    public bool IsEnabled { get; set; } = false;
    public bool ConsoleEnabled { get; set; } = false;
    public bool PublicEnabled { get; set; } = false;

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

    public string Schedule { get; set; } = "";

    public bool ScheduleEnabled { get; set; } = false;

    public string WebhookUrl { get; set; } = "";

    public DateTime? NextRunAt { get; set; }
    public string LastResult { get; set; } = "";
}

public class AppSettings
{
    public int Id { get; set; } = 1;
    public string AppName { get; set; } = "Baseport";
    public string SiteUrl { get; set; } = "";
    public int LogRetentionSec { get; set; } = 604800;
    public string Currency { get; set; } = "EUR";

    public string TimeZone { get; set; } = TimeZones.HostDefault;
    public int BackupRetention { get; set; } = 5;
    public int UploadsMaxMegabytes { get; set; } = 10240;

    public bool S3ExportEnabled { get; set; } = false;
    public string S3Bucket { get; set; } = "";
    public string S3Region { get; set; } = "us-east-1";

    public string S3ServiceUrl { get; set; } = "";
    public string S3AccessKey { get; set; } = "";

    public string S3SecretKeyProtected { get; set; } = "";

    public string S3Prefix { get; set; } = "";

    public string PreviewSecret { get; set; } = "";

    public bool PublicAuthEnabled { get; set; } = false;

    public bool PublicRegistrationEnabled { get; set; } = false;

    public bool AnonymousAuthEnabled { get; set; } = false;

    public int AnonymousRetentionDays { get; set; } = 30;

    public string AuthIssuer { get; set; } = "baseport";

    public int AuthTokenLifetimeSec { get; set; } = 3600;
    public int AuthRefreshLifetimeDays { get; set; } = 30;

    public string AllowedOrigins { get; set; } = "";

    public bool OpenApiEnabled { get; set; } = true;

    public string ApiTitle { get; set; } = "REST API";

    public string ApiDescription { get; set; } =
        "A secure REST API for managing your resources. All endpoints are under `/api/v1/` and require a bearer token.";

    public bool ProxyPrivateTargetsEnabled { get; set; } = false;

    public bool PostgresEnabled { get; set; } = false;
    public int PostgresPort { get; set; } = 5432;
    public string PostgresBindAddress { get; set; } = "127.0.0.1";

    public bool TdsEnabled { get; set; } = false;
    public int TdsPort { get; set; } = 1433;
    public string TdsBindAddress { get; set; } = "127.0.0.1";
}

public class AuditLog
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public string UserId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public string TableName { get; set; } = "";
    public string Message { get; set; } = "";
    public string ClientIp { get; set; } = "";
}

public class JobConfig
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Schedule { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string LastResult { get; set; } = "";
}