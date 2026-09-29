using System.Text.Json.Serialization;

namespace Baseport;

public sealed record TokenPairDto(
    [property: JsonPropertyName("auth_token")] string AuthToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_at")] long ExpiresAt)
{
    public static TokenPairDto From(UserTokenPair tokens) =>
        new(tokens.AuthToken, tokens.RefreshToken, new DateTimeOffset(tokens.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds());
}

public sealed record AuthStatusDto(
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("sub"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sub = null,
    [property: JsonPropertyName("username"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Username = null,
    [property: JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email = null,
    [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonPropertyName("anonymous"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Anonymous = null)
{
    public static readonly AuthStatusDto SignedOut = new(false);

    public static AuthStatusDto For(UserAccount user) =>
        new(true, user.Id, user.Username, user.Email, user.Role, user.IsAnonymous);
}

public sealed record SignedOutDto([property: JsonPropertyName("signed_out")] bool SignedOut);

public sealed record AccountDeletedDto([property: JsonPropertyName("deleted")] bool Deleted);

public sealed record StoredFileDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("content_type")] string ContentType);

public sealed record FileDeletedDto([property: JsonPropertyName("deleted")] string Deleted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(
    [property: JsonPropertyName("email_or_username")] string? EmailOrUsername,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("totp_code")] string? TotpCode = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("username")] string? Username = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshRequest([property: JsonPropertyName("refresh_token")] string? RefreshToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePasswordRequest(
    [property: JsonPropertyName("current_password")] string? CurrentPassword,
    [property: JsonPropertyName("new_password")] string? NewPassword);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransactionRequest(
    [property: JsonPropertyName("operations")] IReadOnlyList<TransactionOp>? Operations,
    [property: JsonPropertyName("transaction")] bool Transaction = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransactionOp(
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("apiName")] string? ApiName,
    [property: JsonPropertyName("recordId")] string? RecordId = null,
    [property: JsonPropertyName("value")] System.Text.Json.Nodes.JsonObject? Value = null);

public sealed record TransactionIdDto([property: JsonPropertyName("id")] string Id);

public sealed record TransactionResultDto([property: JsonPropertyName("results")] IReadOnlyList<TransactionIdDto> Results);

public sealed record TransactionCompletedDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string Id);

public sealed record RecordChangeDto(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("record")] System.Text.Json.Nodes.JsonNode? Record);
