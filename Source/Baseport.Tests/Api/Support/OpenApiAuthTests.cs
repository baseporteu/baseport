using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class OpenApiAuthTests
{
    private static TableDefinition Orders() => new()
    {
        Id = Ids.NewShortId(12),
        Name = "Orders",
        ApiName = "orders",
        ApiEnabled = true,
        Fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), TableId = "x", Name = "total", DataType = "number" }
        }
    };

    private static AppSettings Open(bool registration = true, bool anonymous = true) => new()
    {
        PublicAuthEnabled = true,
        PublicRegistrationEnabled = registration,
        AnonymousAuthEnabled = anonymous
    };

    private static JsonObject Doc(AppSettings settings, params OidcButton[] providers) =>
        OpenApiSpec.BuildDocument(new DocumentInputs([Orders()], settings, "1", providers, []));

    private static JsonObject Paths(JsonObject doc) => doc["paths"]!.AsObject();

    private static JsonObject Schema(JsonObject doc, string name) => doc["components"]!["schemas"]![name]!.AsObject();

    private static List<string> Props(JsonObject schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static List<string> Keys<T>(T value) =>
        JsonSerializer.SerializeToNode(value)!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static List<string> Schemes(JsonNode? operation) =>
        operation!["security"]!.AsArray().SelectMany(r => r!.AsObject().Select(p => p.Key)).ToList();

    [Fact]
    public void AuthOffPublishesNoAuth()
    {
        var doc = Doc(new AppSettings { PublicRegistrationEnabled = true, AnonymousAuthEnabled = true }, new OidcButton("acme", "Acme"));
        var json = doc.ToJsonString();

        Assert.DoesNotContain(Paths(doc), p => p.Key.StartsWith("/api/auth", StringComparison.Ordinal));
        Assert.DoesNotContain(OpenApiSpec.AuthTag, json);
        Assert.DoesNotContain("Auth.", json);
        Assert.DoesNotContain(OpenApiSpec.UserJwtScheme, json);
    }

    [Fact]
    public void AuthOnPublishesCoreRoutes()
    {
        var paths = Paths(Doc(Open(registration: false, anonymous: false)));

        Assert.NotNull(paths["/api/auth/v1/login"]?["post"]);
        Assert.NotNull(paths["/api/auth/v1/refresh"]?["post"]);
        Assert.NotNull(paths["/api/auth/v1/logout"]?["post"]);
        Assert.NotNull(paths["/api/auth/v1/status"]?["get"]);
        Assert.NotNull(paths["/api/auth/v1/change_password"]?["post"]);
        Assert.NotNull(paths["/api/auth/v1/delete"]?["delete"]);
        Assert.NotNull(paths["/api/auth/v1/jwks.json"]?["get"]);
        Assert.Null(paths["/api/auth/v1/register"]);
        Assert.Null(paths["/api/auth/v1/anonymous"]);
        Assert.Null(paths["/api/auth/oidc/{slug}/start"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OptionalRoutesFollowTheirSwitch(bool registration, bool anonymous)
    {
        var paths = Paths(Doc(Open(registration, anonymous)));

        Assert.Equal(registration, paths.ContainsKey("/api/auth/v1/register"));
        Assert.Equal(anonymous, paths.ContainsKey("/api/auth/v1/anonymous"));
    }

    [Fact]
    public void GatesMatchTheRoutes()
    {
        Assert.False(UserAuthEndpoints.RegistrationOpen(new AppSettings { PublicRegistrationEnabled = true }));
        Assert.False(UserAuthEndpoints.AnonymousOpen(new AppSettings { AnonymousAuthEnabled = true }));
        Assert.True(UserAuthEndpoints.RegistrationOpen(Open()));
        Assert.True(UserAuthEndpoints.AnonymousOpen(Open()));
    }

    [Fact]
    public void OidcStartListsPublicProviders()
    {
        var start = Paths(Doc(Open(), new OidcButton("acme", "Acme"), new OidcButton("corp", "Corp")))["/api/auth/oidc/{slug}/start"]!["get"]!;
        var parameters = start["parameters"]!.AsArray();

        var slug = parameters.Single(p => p!["name"]!.GetValue<string>() == "slug")!;
        Assert.Equal(new[] { "acme", "corp" }, slug["schema"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        var surface = parameters.Single(p => p!["name"]!.GetValue<string>() == "surface")!;
        Assert.True(surface["required"]!.GetValue<bool>());
        Assert.NotNull(start["responses"]!["302"]);
    }

    [Fact]
    public void AnonymousRoutesCarryNoSecurity()
    {
        var paths = Paths(Doc(Open(), new OidcButton("acme", "Acme")));

        foreach (var (path, verb) in new[]
        {
            ("/api/auth/v1/login", "post"), ("/api/auth/v1/refresh", "post"), ("/api/auth/v1/logout", "post"),
            ("/api/auth/v1/jwks.json", "get"), ("/api/auth/v1/register", "post"), ("/api/auth/v1/anonymous", "post"),
            ("/api/auth/oidc/{slug}/start", "get")
        })
            Assert.Empty(paths[path]![verb]!["security"]!.AsArray());
    }

    [Fact]
    public void SelfServiceRoutesTakeUserJwtOnly()
    {
        var paths = Paths(Doc(Open()));

        Assert.Equal(new[] { OpenApiSpec.UserJwtScheme }, Schemes(paths["/api/auth/v1/change_password"]!["post"]));
        Assert.Equal(new[] { OpenApiSpec.UserJwtScheme }, Schemes(paths["/api/auth/v1/delete"]!["delete"]));

        var status = paths["/api/auth/v1/status"]!["get"]!["security"]!.AsArray();
        Assert.Contains(status, r => r!.AsObject().Count == 0);
        Assert.Contains(status, r => r!.AsObject().ContainsKey(OpenApiSpec.UserJwtScheme));
    }

    [Fact]
    public void ResponseSchemasMatchDtos()
    {
        var doc = Doc(Open());
        var user = new UserAccount { Id = "u", Username = "n", Email = "e", Role = AccountRoles.User };

        Assert.Equal(Keys(TokenPairDto.From(new UserTokenPair("a", "r", DateTime.UtcNow))), Props(Schema(doc, "Auth.TokenPair")));
        Assert.Equal(Keys(AuthStatusDto.For(user)), Props(Schema(doc, "Auth.Status")));
        Assert.Equal(Keys(new SignedOutDto(true)), Props(Schema(doc, "Auth.SignedOut")));
        Assert.Equal(Keys(new AccountDeletedDto(true)), Props(Schema(doc, "Auth.AccountDeleted")));
    }

    [Fact]
    public void RequestSchemasMatchDtos()
    {
        var doc = Doc(Open());

        Assert.Equal(Keys(new LoginRequest("a", "b", "c")), Props(Schema(doc, "Auth.LoginRequest")));
        Assert.Equal(Keys(new RegisterRequest("a", "b", "c")), Props(Schema(doc, "Auth.RegisterRequest")));
        Assert.Equal(Keys(new RefreshRequest("a")), Props(Schema(doc, "Auth.RefreshRequest")));
        Assert.Equal(Keys(new ChangePasswordRequest("a", "b")), Props(Schema(doc, "Auth.ChangePasswordRequest")));
    }

    [Fact]
    public void PasswordsAreWriteOnly()
    {
        var doc = Doc(Open());

        foreach (var (schema, member) in new[]
        {
            ("Auth.LoginRequest", "password"), ("Auth.RegisterRequest", "password"),
            ("Auth.ChangePasswordRequest", "current_password"), ("Auth.ChangePasswordRequest", "new_password")
        })
        {
            var property = Schema(doc, schema)["properties"]![member]!;
            Assert.True(property["writeOnly"]!.GetValue<bool>());
            Assert.Equal("password", property["format"]!.GetValue<string>());
        }
    }

    [Fact]
    public void LoginDocumentsTheSecondFactor()
    {
        var unauthorized = Paths(Doc(Open()))["/api/auth/v1/login"]!["post"]!["responses"]!["401"]!;

        Assert.Contains("totp", unauthorized["description"]!.GetValue<string>());
        Assert.Equal($"#/components/schemas/{OpenApiSpec.ProblemSchema}",
            unauthorized["content"]![ApiProblems.ContentType]!["schema"]!["$ref"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"email_or_username":"a","password":"b","totp":"1"}""")]
    [InlineData("""{"email_or_username":"a","password":"b","extra":true}""")]
    public void LoginRefusesUnknownMembers(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>(body, JsonSerializerOptions.Web));
    }

    [Fact]
    public void LoginAcceptsTheDocumentedMembers()
    {
        var request = JsonSerializer.Deserialize<LoginRequest>("""{"email_or_username":"a","password":"b","totp_code":"123456"}""", JsonSerializerOptions.Web)!;

        Assert.Equal("a", request.EmailOrUsername);
        Assert.Equal("b", request.Password);
        Assert.Equal("123456", request.TotpCode);
    }

    [Fact]
    public void TableNamesCannotShadowBuiltIns()
    {
        var error = Orders();
        error.ApiName = "error";
        var auth = Orders();
        auth.ApiName = "api-auth";

        var doc = OpenApiSpec.BuildDocument(new DocumentInputs([error, auth], Open(), "1", [], []));
        var schemas = doc["components"]!["schemas"]!.AsObject();
        var tags = doc["tags"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.Equal("RFC 9457 problem details", schemas[OpenApiSpec.ProblemSchema]!["description"]!.GetValue<string>()[..24]);
        Assert.True(schemas.ContainsKey("Error"));
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(OpenApiSpec.AuthTag, tags);
    }
}
