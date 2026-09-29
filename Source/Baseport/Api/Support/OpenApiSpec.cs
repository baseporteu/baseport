using System.Text.Json.Nodes;

namespace Baseport;

public sealed record DocumentInputs(
    IReadOnlyList<TableDefinition> Tables,
    AppSettings Settings,
    string ProductVersion,
    IReadOnlyList<OidcButton> PublicProviders,
    IReadOnlyList<Bucket> Buckets);

public static partial class OpenApiSpec
{
    public static JsonObject BuildDocument(DocumentInputs inputs)
    {
        var tables = inputs.Tables.Where(t => t.ApiEnabled && t.ApiDocsEnabled).OrderBy(t => t.ApiName, StringComparer.Ordinal).ToList();
        var jwt = UserAuthEndpoints.Enabled(inputs.Settings);
        var tags = BuildTags(tables);
        var paths = BuildPaths(tables, jwt);
        var schemas = BuildSchemas(tables);
        if (jwt)
        {
            tags.Add(AuthTagNode());
            Merge(paths, AuthPaths(inputs.Settings, inputs.PublicProviders));
            Merge(schemas, AuthSchemas());
        }

        if (Writable(tables) is { Count: > 0 } writable)
        {
            tags.Add(TransactionTagNode());
            Merge(paths, TransactionPaths(jwt));
            Merge(schemas, TransactionSchemas(writable));
        }

        var buckets = inputs.Buckets.Where(b => b.ApiEnabled && BucketMethods.Parse(b.ApiMethods).Count > 0).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        if (buckets.Count > 0)
        {
            tags.Add(StorageTagNode());
            foreach (var bucket in buckets) tags.Add(BucketTagNode(bucket));
            Merge(paths, StoragePaths(buckets, jwt));
            Merge(schemas, StorageSchemas());
        }

        return new JsonObject
        {
            ["openapi"] = "3.2.0",
            ["info"] = new JsonObject
            {
                ["title"] = inputs.Settings.ApiTitle,
                ["version"] = inputs.ProductVersion,
                ["description"] = inputs.Settings.ApiDescription
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["tags"] = tags,
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = SecuritySchemes(inputs.Settings),
                ["schemas"] = schemas
            }
        };
    }

    private static JsonObject SecuritySchemes(AppSettings settings)
    {
        var schemes = new JsonObject
        {
            [ApiTokenScheme] = new JsonObject
            {
                ["type"] = "http",
                ["scheme"] = "bearer",
                ["description"] = "Static token issued by an operator to one account. Shown once at issue, valid until its expiry date, refused while the account's API access is off."
            }
        };
        if (UserAuthEndpoints.Enabled(settings))
            schemes[UserJwtScheme] = new JsonObject
            {
                ["type"] = "http",
                ["scheme"] = "bearer",
                ["bearerFormat"] = "JWT",
                ["description"] = $"ES256 token from `POST /api/auth/v1/login`, valid for {settings.AuthTokenLifetimeSec} seconds. Renew with `POST /api/auth/v1/refresh`. Verification key: `GET /api/auth/v1/jwks.json`."
            };
        return schemes;
    }

    private static JsonArray Bearer(bool jwt)
    {
        var security = new JsonArray(new JsonObject { [ApiTokenScheme] = new JsonArray() });
        if (jwt) security.Add(new JsonObject { [UserJwtScheme] = new JsonArray() });
        return security;
    }

    private static JsonArray Anonymous() => new();

    public const string ApiTokenScheme = "ApiToken";

    public const string UserJwtScheme = "UserJwt";

    public const string ProblemSchema = "Api.Error";

    public const string BuiltInTagPrefix = "api:";

    private static JsonObject SchemaRef(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    private static JsonObject JsonBody(string schema, string? description = null)
    {
        var body = new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = SchemaRef(schema) } }
        };
        if (description is not null) body["description"] = description;
        return body;
    }

    private static JsonObject Operation(string tag, string operationId, string summary, JsonArray security, JsonObject responses,
        JsonObject? requestBody = null, JsonArray? parameters = null, string? description = null)
    {
        var op = new JsonObject
        {
            ["operationId"] = operationId,
            ["summary"] = summary,
            ["tags"] = new JsonArray((JsonNode)tag),
            ["security"] = security,
            ["responses"] = responses
        };
        if (description is not null) op["description"] = description;
        if (parameters is not null) op["parameters"] = parameters;
        if (requestBody is not null) op["requestBody"] = requestBody;
        return op;
    }

    private static JsonObject Outcomes(IEnumerable<(string Code, JsonObject Response)> success, IEnumerable<(ApiProblem Problem, string Description)> problems)
    {
        var responses = new JsonObject();
        foreach (var (code, response) in success) responses[code] = response;
        foreach (var (problem, description) in problems)
            responses[problem.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ProblemResp(problem, description);
        return responses;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var name in source.Select(p => p.Key).ToList())
        {
            var node = source[name];
            source.Remove(name);
            target[name] = node;
        }
    }

    private static JsonNode Param(string name, string description, string type) => new JsonObject
    {
        ["name"] = name,
        ["in"] = "query",
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = type }
    };

    private static JsonObject JsonResp(string description, JsonObject? schema)
    {
        if (schema == null) return new JsonObject { ["description"] = description };
        return new JsonObject
        {
            ["description"] = description,
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } }
        };
    }

    private static JsonObject ProblemResp(ApiProblem problem, string description)
    {
        var response = new JsonObject
        {
            ["description"] = description,
            ["content"] = new JsonObject { [ApiProblems.ContentType] = new JsonObject { ["schema"] = ErrorResponse() } }
        };
        if (problem.Status == 405) response["headers"] = Header("Allow", "The methods this endpoint does answer.");
        if (problem.Status == 429) response["headers"] = Header("Retry-After", "Seconds to wait before retrying.");
        return response;
    }

    private static JsonObject Header(string name, string description) => new()
    {
        [name] = new JsonObject
        {
            ["description"] = description,
            ["schema"] = new JsonObject { ["type"] = "string" }
        }
    };

    private static JsonNode HeaderParam(string name, string description) => new JsonObject
    {
        ["name"] = name,
        ["in"] = "header",
        ["required"] = false,
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = "string" }
    };

    private static JsonObject ErrorResponse() => new()
    {
        ["$ref"] = $"#/components/schemas/{ProblemSchema}"
    };
}
