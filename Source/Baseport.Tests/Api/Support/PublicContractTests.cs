using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class PublicContractTests
{
    private static JsonObject Serialize<T>(T value) => JsonSerializer.SerializeToNode(value)!.AsObject();

    private static IEnumerable<string> Keys(JsonObject o) => o.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal);

    [Fact]
    public void TokenPairWireNames()
    {
        var dto = TokenPairDto.From(new UserTokenPair("a", "r", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var json = Serialize(dto);

        Assert.Equal(new[] { "auth_token", "expires_at", "refresh_token" }, Keys(json));
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), json["expires_at"]!.GetValue<long>());
    }

    [Fact]
    public void SignedOutStatusCarriesOnlyTheFlag()
    {
        Assert.Equal(new[] { "authenticated" }, Keys(Serialize(AuthStatusDto.SignedOut)));
    }

    [Fact]
    public void SignedInStatusWireNames()
    {
        var user = new UserAccount { Id = "u1", Username = "jane", Email = "j@example.com", Role = AccountRoles.User, IsAnonymous = true };

        var json = Serialize(AuthStatusDto.For(user));

        Assert.Equal(new[] { "anonymous", "authenticated", "email", "role", "sub", "username" }, Keys(json));
        Assert.True(json["anonymous"]!.GetValue<bool>());
        Assert.Equal("u1", json["sub"]!.GetValue<string>());
    }

    [Fact]
    public void StoredFileWireNames()
    {
        var json = Serialize(new StoredFileDto("b/x.png", "b", "x.png", "http://h/uploads/b/x.png", 3, "image/png"));

        Assert.Equal(new[] { "bucket", "content_type", "id", "name", "size", "url" }, Keys(json));
    }

    [Fact]
    public void SmallResultWireNames()
    {
        Assert.Equal(new[] { "signed_out" }, Keys(Serialize(new SignedOutDto(true))));
        Assert.Equal(new[] { "deleted" }, Keys(Serialize(new AccountDeletedDto(true))));
        Assert.Equal(new[] { "deleted" }, Keys(Serialize(new FileDeletedDto("b/x.png"))));
    }

    [Fact]
    public void RecordChangeWireNames()
    {
        var created = Serialize(new RecordChangeDto("create", "r1", new JsonObject { ["a"] = 1 }));
        var deleted = Serialize(new RecordChangeDto("delete", "r1", null));

        Assert.Equal(new[] { "action", "id", "record" }, Keys(created));
        Assert.Equal(new[] { "action", "id", "record" }, Keys(deleted));
        Assert.Null(deleted["record"]);
    }

    [Fact]
    public void ProblemCarriesExtensions()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/auth/v1/login";

        var body = ApiProblems.Body(ctx, ApiProblem.Unauthorized, Totp.CodeNeeded, extensions: Totp.RequiredExtension);

        Assert.Equal(true, body["totp"]);
        Assert.Equal(401, body["status"]);
        Assert.Equal(new[] { Totp.CodeNeeded }, (IReadOnlyList<string>)body["errors"]!);
    }

    [Fact]
    public void ExtensionsCannotOverrideStandardMembers()
    {
        var ctx = new DefaultHttpContext();
        var extensions = new Dictionary<string, object?> { ["status"] = 200, ["type"] = "spoofed" };

        var body = ApiProblems.Body(ctx, ApiProblem.Forbidden, "no", extensions: extensions);

        Assert.Equal(403, body["status"]);
        Assert.Equal(ApiProblem.Forbidden.Type, body["type"]);
    }

    [Theory]
    [InlineData(413, 413)]
    [InlineData(404, 404)]
    [InlineData(418, 400)]
    [InlineData(599, 500)]
    public void ProblemForStatus(int status, int expected)
    {
        Assert.Equal(expected, ApiProblem.ForStatus(status).Status);
    }

    [Theory]
    [InlineData("/api/auth/v1/login", BodyLimits.AuthBytes)]
    [InlineData("/api/auth/login", BodyLimits.AuthBytes)]
    [InlineData("/api/auth/oidc/acme/link", BodyLimits.AuthBytes)]
    [InlineData("/api/transaction/v1/execute", BodyLimits.TransactionBytes)]
    [InlineData("/api/v1/orders/records", null)]
    [InlineData("/api/authority", null)]
    [InlineData("/api/forms/x/form", null)]
    public void BodyLimitByPath(string path, long? expected)
    {
        Assert.Equal(expected, BodyLimits.For(new PathString(path)));
    }
}
