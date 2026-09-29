using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public partial class ApiContractTests
{
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["/api/openapi.json"] = "the document itself",
        ["/api/client-errors"] = "browser error beacon for the console and embed",
        ["/api/auth/login"] = "console sign-in, cookie session",
        ["/api/auth/otp"] = "console one-time code, cookie session",
        ["/api/auth/logout"] = "console sign-out, cookie session",
        ["/api/auth/me"] = "console session, cookie session",
        ["/api/auth/password"] = "console password change, cookie session",
        ["/api/auth/totp"] = "console two-factor, cookie session",
        ["/api/auth/totp/setup"] = "console two-factor, cookie session",
        ["/api/auth/totp/confirm"] = "console two-factor, cookie session",
        ["/api/auth/oidc/{}/callback"] = "identity provider redirect target",
        ["/api/auth/oidc/{}/link"] = "console account linking",
    };

    private static readonly string[] ExcludedPrefixes =
    [
        "/api/_admin/",
        "/api/forms/"
    ];

    private static string RepoSource()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Baseport.slnx"))) root = Path.GetDirectoryName(root)!;
        return root;
    }

    private static List<(string Pattern, string Method)> Routes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.Combine(RepoSource(), "Baseport"),
            EnvironmentName = "Production"
        });
        var connection = new SqliteConnection("Filename=:memory:");
        builder.Services.AddDbContext<AppDbContext>(o => AppDbContext.Configure(o, connection));
        builder.Services.AddOutboundHttp();
        builder.Services.AddBaseportRateLimiter();
        builder.Services.AddSingleton<AuditLogWriter>();
        builder.Services.AddSingleton<ImportRunner>();

        var app = builder.Build();
        app.MapBaseportEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => (Pattern: "/" + e.RoutePattern.RawText!.TrimStart('/'), Methods: e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(e => e.Pattern.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(e => e.Methods.Select(m => (Normalize(e.Pattern), m.ToUpperInvariant())))
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();

    private static string Normalize(string pattern) => Parameter().Replace(pattern, "{}");

    private static TableDefinition Table(string apiName) => new()
    {
        Id = Ids.NewShortId(12),
        Name = apiName,
        ApiName = apiName,
        ApiEnabled = true,
        ApiNamespace = "Sales",
        Fields = [new() { Id = Ids.NewShortId(12), TableId = "x", Name = "body", DataType = "text", IsRequired = true }]
    };

    private static DocumentInputs Open() => new(
        [Table("orders")],
        new AppSettings { PublicAuthEnabled = true, PublicRegistrationEnabled = true, AnonymousAuthEnabled = true },
        "1.0.0",
        [new OidcButton("acme", "Acme")],
        [new Bucket { Id = "b1", Name = "avatars", ApiEnabled = true, AllowJwt = true, ContentTypes = "image/*" }]);

    private static DocumentInputs Closed() => new([], new AppSettings(), "1.0.0", [], []);

    private static List<(string Pattern, string Method)> Documented(JsonObject doc)
    {
        var literals = new[] { ("/api/v1/orders/", "/api/v1/{}/"), ("/api/v1/files/avatars", "/api/v1/files/{}"), ("/api/auth/oidc/acme/", "/api/auth/oidc/{}/") };
        var verbs = new HashSet<string>(StringComparer.Ordinal) { "get", "put", "post", "delete", "options", "head", "patch", "trace", "query" };
        return doc["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Where(o => verbs.Contains(o.Key)).Select(o =>
            {
                var path = literals.Aggregate(p.Key, (acc, l) => acc.StartsWith(l.Item1, StringComparison.Ordinal) ? l.Item2 + acc[l.Item1.Length..] : acc);
                return (Normalize(path), o.Key.ToUpperInvariant());
            }))
            .ToList();
    }

    private static bool IsExcluded(string pattern) =>
        Excluded.ContainsKey(pattern) || ExcludedPrefixes.Any(p => pattern.StartsWith(p, StringComparison.Ordinal));

    [Fact]
    public void EveryPublicRouteIsDocumented()
    {
        var documented = Documented(OpenApiSpec.BuildDocument(Open())).ToHashSet();

        var missing = Routes().Where(r => !IsExcluded(r.Pattern) && !documented.Contains(r)).Select(r => $"{r.Method} {r.Pattern}").ToList();

        Assert.True(missing.Count == 0, "Undocumented public routes: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDocumentedOperationIsARoute()
    {
        var routes = Routes().ToHashSet();

        var phantom = Documented(OpenApiSpec.BuildDocument(Open())).Where(d => !routes.Contains(d)).Select(d => $"{d.Method} {d.Pattern}").ToList();

        Assert.True(phantom.Count == 0, "Documented but not routed: " + string.Join(", ", phantom));
    }

    [Fact]
    public void ExclusionsStillExist()
    {
        var patterns = Routes().Select(r => r.Pattern).ToHashSet(StringComparer.Ordinal);

        Assert.All(Excluded.Keys, key => Assert.Contains(key, patterns));
    }

    [Fact]
    public void ExcludedRoutesAreNotDocumented()
    {
        var documented = Documented(OpenApiSpec.BuildDocument(Open()));

        Assert.DoesNotContain(documented, d => IsExcluded(d.Pattern));
    }

    private static readonly Lazy<JsonSchema> OasSchema = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "oas-3.2-schema.json"))
            .Replace("\"^/\"", "\"^\\\\x2F\"", StringComparison.Ordinal)));

    private static void AssertValid(JsonObject doc)
    {
        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        var result = OasSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Take(20);
        Assert.True(result.IsValid, "OpenAPI 3.2 schema violations: " + string.Join(" | ", errors));
    }

    [Fact]
    public void OpenDocumentIsValidOpenApi32() => AssertValid(OpenApiSpec.BuildDocument(Open()));

    [Fact]
    public void ClosedDocumentIsValidOpenApi32() => AssertValid(OpenApiSpec.BuildDocument(Closed()));

    [Fact]
    public void DocumentWithEveryFieldTypeIsValidOpenApi32()
    {
        var table = Table("everything");
        table.ApiNamespace = "";
        table.ApiDisplayName = "Everything";
        table.Fields = FieldTypes.All.Select(t => new FieldDefinition
        {
            Id = Ids.NewShortId(12),
            TableId = "x",
            Name = "f_" + t.Name,
            DataType = t.Name,
            Min = 1,
            Max = 10,
            OptionsJson = t.Name is "select" or "multiselect" ? """["a","b"]""" : ""
        }).ToList();

        AssertValid(OpenApiSpec.BuildDocument(Open() with { Tables = [table, Table("orders")] }));
    }

    [Theory]
    [InlineData("/api/auth/v1/login", "post")]
    [InlineData("/api/v1/orders/records", "get")]
    [InlineData("/api/v1/files/avatars", "post")]
    public void BrokenOperationFailsValidation(string path, string verb)
    {
        var doc = OpenApiSpec.BuildDocument(Open());
        doc["paths"]![path]![verb]!.AsObject()["parameters"] = new JsonArray(new JsonObject { ["name"] = "x" });

        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        Assert.False(OasSchema.Value.Evaluate(parsed.RootElement).IsValid);
    }

    [Fact]
    public void BrokenDocumentFailsValidation()
    {
        var doc = OpenApiSpec.BuildDocument(Closed());
        doc["info"]!.AsObject().Remove("version");

        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        Assert.False(OasSchema.Value.Evaluate(parsed.RootElement).IsValid);
    }
}
