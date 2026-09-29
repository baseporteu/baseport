using System.Text.Json.Nodes;

namespace Baseport;

public static partial class OpenApiSpec
{
    public const string AuthTag = BuiltInTagPrefix + "auth";

    private const string TokenPairSchema = "Auth.TokenPair";
    private const string StatusSchema = "Auth.Status";
    private const string LoginSchema = "Auth.LoginRequest";
    private const string RegisterSchema = "Auth.RegisterRequest";
    private const string RefreshSchema = "Auth.RefreshRequest";
    private const string ChangePasswordSchema = "Auth.ChangePasswordRequest";
    private const string SignedOutSchema = "Auth.SignedOut";
    private const string AccountDeletedSchema = "Auth.AccountDeleted";
    private const string JwksSchema = "Auth.Jwks";

    private static JsonObject AuthTagNode() => new()
    {
        ["name"] = AuthTag,
        ["summary"] = "Authentication",
        ["description"] = "End-user accounts: sign-in, token renewal and self-service. Tokens are ES256 JWTs, verifiable with the published key set."
    };

    private static JsonObject AuthPaths(AppSettings settings, IReadOnlyList<OidcButton> providers)
    {
        var paths = new JsonObject();

        paths["/api/auth/v1/login"] = new JsonObject
        {
            ["post"] = Operation(AuthTag, "auth_login", "Sign in", Anonymous(),
                Outcomes([("200", JsonResp("Token pair", SchemaRef(TokenPairSchema)))],
                [
                    (ApiProblem.BadRequest, "Malformed body or unknown member"),
                    (ApiProblem.Unauthorized, "Incorrect credentials, or an authenticator code is required (`totp: true`) or invalid"),
                    (ApiProblem.TooLarge, "Body over the authentication limit"),
                    (ApiProblem.TooManyRequests, "Rate limit or sign-in lockout")
                ]),
                JsonBody(LoginSchema))
        };

        paths["/api/auth/v1/refresh"] = new JsonObject
        {
            ["post"] = Operation(AuthTag, "auth_refresh", "Renew the auth token", Anonymous(),
                Outcomes([("200", JsonResp("Token pair", SchemaRef(TokenPairSchema)))],
                [
                    (ApiProblem.BadRequest, "Malformed body or unknown member"),
                    (ApiProblem.Unauthorized, "Refresh token unknown, revoked or expired"),
                    (ApiProblem.TooLarge, "Body over the authentication limit"),
                    (ApiProblem.TooManyRequests, "Rate limit exceeded")
                ]),
                JsonBody(RefreshSchema), description: "The refresh token is not rotated; it stays valid until it expires or is revoked.")
        };

        paths["/api/auth/v1/logout"] = new JsonObject
        {
            ["post"] = Operation(AuthTag, "auth_logout", "Revoke a refresh token", Anonymous(),
                Outcomes([("200", JsonResp("Signed out", SchemaRef(SignedOutSchema)))],
                [
                    (ApiProblem.BadRequest, "Malformed body or unknown member"),
                    (ApiProblem.TooLarge, "Body over the authentication limit")
                ]),
                JsonBody(RefreshSchema))
        };

        paths["/api/auth/v1/status"] = new JsonObject
        {
            ["get"] = Operation(AuthTag, "auth_status", "Current account", Optional(UserOnly()),
                Outcomes([("200", JsonResp("The calling account, or `authenticated: false`", SchemaRef(StatusSchema)))], []))
        };

        paths["/api/auth/v1/change_password"] = new JsonObject
        {
            ["post"] = Operation(AuthTag, "auth_change_password", "Change the password", UserOnly(),
                Outcomes([("200", JsonResp("New token pair; every other session is revoked", SchemaRef(TokenPairSchema)))],
                [
                    (ApiProblem.BadRequest, "Malformed body or unknown member"),
                    (ApiProblem.Unauthorized, "Missing or invalid token"),
                    (ApiProblem.Forbidden, "Current password incorrect"),
                    (ApiProblem.TooLarge, "Body over the authentication limit"),
                    (ApiProblem.Unprocessable, "New password rejected by the password policy"),
                    (ApiProblem.TooManyRequests, "Rate limit or lockout")
                ]),
                JsonBody(ChangePasswordSchema))
        };

        paths["/api/auth/v1/delete"] = new JsonObject
        {
            ["delete"] = Operation(AuthTag, "auth_delete", "Delete the calling account", UserOnly(),
                Outcomes([("200", JsonResp("Deleted", SchemaRef(AccountDeletedSchema)))],
                [
                    (ApiProblem.Unauthorized, "Missing or invalid token"),
                    (ApiProblem.Conflict, "Last enabled account or last enabled admin"),
                    (ApiProblem.TooManyRequests, "Rate limit exceeded")
                ]))
        };

        paths["/api/auth/v1/jwks.json"] = new JsonObject
        {
            ["get"] = Operation(AuthTag, "auth_jwks", "Token verification key set", Anonymous(),
                Outcomes([("200", JsonResp("JWK Set (RFC 7517)", SchemaRef(JwksSchema)))],
                [(ApiProblem.TooManyRequests, "Rate limit exceeded")]))
        };

        if (UserAuthEndpoints.RegistrationOpen(settings))
            paths["/api/auth/v1/register"] = new JsonObject
            {
                ["post"] = Operation(AuthTag, "auth_register", "Create an account", Anonymous(),
                    Outcomes([("201", JsonResp("Token pair for the new account", SchemaRef(TokenPairSchema)))],
                    [
                        (ApiProblem.BadRequest, "Malformed body or unknown member"),
                        (ApiProblem.Conflict, "Username or email already registered"),
                        (ApiProblem.TooLarge, "Body over the authentication limit"),
                        (ApiProblem.Unprocessable, "Username, email or password rejected"),
                        (ApiProblem.TooManyRequests, "Rate limit exceeded")
                    ]),
                    JsonBody(RegisterSchema),
                    description: "Sent with an anonymous account's token, converts that account instead of creating a new one.")
            };

        if (UserAuthEndpoints.AnonymousOpen(settings))
            paths["/api/auth/v1/anonymous"] = new JsonObject
            {
                ["post"] = Operation(AuthTag, "auth_anonymous", "Create an anonymous account", Anonymous(),
                    Outcomes([("201", JsonResp("Token pair for the anonymous account", SchemaRef(TokenPairSchema)))],
                    [(ApiProblem.TooManyRequests, "Rate limit exceeded")]))
            };

        if (providers.Count > 0)
            paths["/api/auth/oidc/{slug}/start"] = new JsonObject
            {
                ["get"] = Operation(AuthTag, "auth_oidc_start", "Sign in with an identity provider", Anonymous(),
                    Outcomes(
                    [
                        ("302", new JsonObject
                        {
                            ["description"] = "Redirect to the provider. The browser returns to `/auth/profile` signed in.",
                            ["headers"] = Header("Location", "Provider authorization URL.")
                        })
                    ],
                    [
                        (ApiProblem.NotFound, "Unknown or disabled provider"),
                        (ApiProblem.TooManyRequests, "Rate limit exceeded")
                    ]),
                    parameters: new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "slug",
                            ["in"] = "path",
                            ["required"] = true,
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray(providers.Select(p => (JsonNode)p.Slug).ToArray())
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "surface",
                            ["in"] = "query",
                            ["required"] = true,
                            ["schema"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("public") }
                        }),
                    description: "A browser navigation, not an XHR call.")
            };

        return paths;
    }

    private static JsonArray UserOnly() => new(new JsonObject { [UserJwtScheme] = new JsonArray() });

    private static JsonArray Optional(JsonArray security)
    {
        security.Add(new JsonObject());
        return security;
    }

    private static JsonObject AuthSchemas() => new()
    {
        [TokenPairSchema] = Closed(
            ("auth_token", Str("ES256 JWT. Send as `Authorization: Bearer`.")),
            ("refresh_token", Str("Opaque. Exchange at `/api/auth/v1/refresh`.")),
            ("expires_at", new JsonObject { ["type"] = "integer", ["format"] = "int64", ["description"] = "Unix seconds when `auth_token` expires." })),
        [StatusSchema] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["authenticated"] = new JsonObject { ["type"] = "boolean" },
                ["sub"] = Str("Account id."),
                ["username"] = Str(null),
                ["email"] = Str(null),
                ["role"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(AccountRoles.Admin, AccountRoles.Consumer, AccountRoles.User) },
                ["anonymous"] = new JsonObject { ["type"] = "boolean" }
            },
            ["required"] = new JsonArray("authenticated"),
            ["additionalProperties"] = false
        },
        [LoginSchema] = Request(["email_or_username", "password"],
            ("email_or_username", Str(null)),
            ("password", Secret()),
            ("totp_code", Str("Six-digit authenticator code, when the account has one."))),
        [RegisterSchema] = Request(["password"],
            ("email", new JsonObject { ["type"] = "string", ["format"] = "email" }),
            ("password", Secret()),
            ("username", Str("Derived from the email when omitted."))),
        [RefreshSchema] = Request(["refresh_token"], ("refresh_token", Str(null))),
        [ChangePasswordSchema] = Request(["current_password", "new_password"],
            ("current_password", Secret()),
            ("new_password", Secret())),
        [SignedOutSchema] = Closed(("signed_out", new JsonObject { ["type"] = "boolean" })),
        [AccountDeletedSchema] = Closed(("deleted", new JsonObject { ["type"] = "boolean" })),
        [JwksSchema] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["keys"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } }
            },
            ["required"] = new JsonArray("keys")
        }
    };

    private static JsonObject Str(string? description)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (description is not null) schema["description"] = description;
        return schema;
    }

    private static JsonObject Secret() => new() { ["type"] = "string", ["format"] = "password", ["writeOnly"] = true };

    private static JsonObject Closed(params (string Name, JsonObject Schema)[] members) => new()
    {
        ["type"] = "object",
        ["properties"] = Properties(members),
        ["required"] = new JsonArray(members.Select(m => (JsonNode)m.Name).ToArray()),
        ["additionalProperties"] = false
    };

    private static JsonObject Request(string[] required, params (string Name, JsonObject Schema)[] members) => new()
    {
        ["type"] = "object",
        ["properties"] = Properties(members),
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        ["additionalProperties"] = false
    };

    private static JsonObject Properties((string Name, JsonObject Schema)[] members)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in members) properties[name] = schema;
        return properties;
    }
}
