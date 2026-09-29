using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class OpenApiStorageTests
{
    private static Bucket Avatars(Action<Bucket>? change = null)
    {
        var bucket = new Bucket { Id = Ids.NewShortId(12), Name = "avatars", ApiEnabled = true, MaxMegabytes = 5 };
        change?.Invoke(bucket);
        return bucket;
    }

    private static JsonObject Doc(bool publicAuth, params Bucket[] buckets) =>
        OpenApiSpec.BuildDocument(new DocumentInputs([], new AppSettings { PublicAuthEnabled = publicAuth }, "1", [], buckets));

    private static JsonObject Paths(JsonObject doc) => doc["paths"]!.AsObject();

    private static List<string> Schemes(JsonNode? operation) =>
        operation!["security"]!.AsArray().SelectMany(r => r!.AsObject().Select(p => p.Key)).ToList();

    [Fact]
    public void NoBucketNoStorage()
    {
        var json = Doc(false).ToJsonString();

        Assert.DoesNotContain("/api/v1/files", json);
        Assert.DoesNotContain(OpenApiSpec.StorageTag, json);
        Assert.DoesNotContain("Storage.File", json);
    }

    [Fact]
    public void DisabledBucketIsAbsent()
    {
        var json = Doc(false, Avatars(b => b.ApiEnabled = false), Avatars(b => { b.Name = "empty"; b.ApiMethods = ""; })).ToJsonString();

        Assert.DoesNotContain("/api/v1/files", json);
        Assert.DoesNotContain(OpenApiSpec.StorageTag, json);
    }

    [Fact]
    public void BucketNameIsALiteralPath()
    {
        var paths = Paths(Doc(false, Avatars()));

        Assert.NotNull(paths["/api/v1/files/avatars"]?["post"]);
        Assert.NotNull(paths["/api/v1/files/avatars/{file}"]?["get"]);
        Assert.NotNull(paths["/api/v1/files/avatars/{file}"]?["delete"]);
        Assert.DoesNotContain(paths, p => p.Key.Contains("{bucket}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET", false, true, false)]
    [InlineData("POST", true, false, false)]
    [InlineData("DELETE", false, false, true)]
    [InlineData("GET,DELETE", false, true, true)]
    public void MethodsFollowTheBucket(string methods, bool post, bool get, bool delete)
    {
        var paths = Paths(Doc(false, Avatars(b => b.ApiMethods = methods)));

        Assert.Equal(post, paths["/api/v1/files/avatars"]?["post"] is not null);
        Assert.Equal(get, paths["/api/v1/files/avatars/{file}"]?["get"] is not null);
        Assert.Equal(delete, paths["/api/v1/files/avatars/{file}"]?["delete"] is not null);
    }

    [Theory]
    [InlineData(false, false, new[] { "ApiToken" })]
    [InlineData(false, true, new[] { "ApiToken" })]
    [InlineData(true, false, new[] { "ApiToken" })]
    [InlineData(true, true, new[] { "ApiToken", "UserJwt" })]
    public void UserJwtOnlyWhereAllowed(bool publicAuth, bool allowJwt, string[] expected)
    {
        var upload = Paths(Doc(publicAuth, Avatars(b => b.AllowJwt = allowJwt)))["/api/v1/files/avatars"]!["post"]!;

        Assert.Equal(expected, Schemes(upload));
        Assert.Equal(publicAuth && !allowJwt, upload["responses"]!["403"] is not null);
    }

    [Fact]
    public void BucketsNestUnderStorage()
    {
        var tags = Doc(false, Avatars(), Avatars(b => b.Name = "docs")).AsObject()["tags"]!.AsArray().Select(t => t!.AsObject()).ToList();

        var storage = Assert.Single(tags, t => t["name"]!.GetValue<string>() == OpenApiSpec.StorageTag);
        Assert.Equal("nav", storage["kind"]!.GetValue<string>());
        var buckets = tags.Where(t => t["parent"]?.GetValue<string>() == OpenApiSpec.StorageTag).Select(t => t["summary"]!.GetValue<string>());
        Assert.Equal(new[] { "avatars", "docs" }, buckets);
    }

    [Fact]
    public void UploadIsMultipartWithAcceptedTypes()
    {
        var upload = Paths(Doc(false, Avatars(b => b.ContentTypes = "image/*,application/pdf")))["/api/v1/files/avatars"]!["post"]!;
        var media = upload["requestBody"]!["content"]!["multipart/form-data"]!;

        Assert.Equal("binary", media["schema"]!["properties"]!["file"]!["format"]!.GetValue<string>());
        Assert.Equal("image/*, application/pdf", media["encoding"]!["file"]!["contentType"]!.GetValue<string>());
        Assert.Contains("5 MB", upload["description"]!.GetValue<string>());
        foreach (var code in new[] { "201", "400", "401", "404", "405", "413", "415", "429", "507" })
            Assert.NotNull(upload["responses"]![code]);
    }

    [Fact]
    public void StoredFileSchemaMatchesDto()
    {
        var schema = Doc(false, Avatars())["components"]!["schemas"]!["Storage.File"]!["properties"]!.AsObject();
        var dto = JsonSerializer.SerializeToNode(new StoredFileDto("a/b.png", "a", "b.png", "http://x/uploads/a/b.png", 1, "image/png"))!.AsObject();

        Assert.Equal(dto.Select(p => p.Key).OrderBy(k => k), schema.Select(p => p.Key).OrderBy(k => k));
        var deleted = Doc(false, Avatars())["components"]!["schemas"]!["Storage.Deleted"]!["properties"]!.AsObject();
        Assert.Equal(JsonSerializer.SerializeToNode(new FileDeletedDto("a/b"))!.AsObject().Select(p => p.Key), deleted.Select(p => p.Key));
    }

    [Fact]
    public void BucketNamedLikeATableDoesNotCollide()
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Avatars", ApiName = "avatars", ApiEnabled = true, Fields = [] };
        var doc = OpenApiSpec.BuildDocument(new DocumentInputs([table], new AppSettings(), "1", [], [Avatars()]));
        var names = doc["tags"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("avatars", names);
        Assert.Contains(OpenApiSpec.BucketTag(Avatars()), names);
    }
}

public class BucketRulesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public BucketRulesTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData("", "image/png", true)]
    [InlineData("image/*", "image/png", true)]
    [InlineData("image/*", "application/pdf", false)]
    [InlineData("application/pdf", "application/pdf", true)]
    [InlineData("application/pdf", "application/pdfx", false)]
    [InlineData("IMAGE/PNG", "image/png", true)]
    [InlineData("image/*", "imagex/png", false)]
    [InlineData("text/plain, application/json", "application/json", true)]
    public void AcceptedTypes(string configured, string type, bool accepted)
    {
        Assert.Equal(accepted, BucketMethods.AcceptsType(new Bucket { ContentTypes = configured }, type));
    }

    [Theory]
    [InlineData("avatars", 5, "", "GET", true, 0)]
    [InlineData("Avatars", 5, "", "GET", true, 1)]
    [InlineData("a/b", 5, "", "GET", true, 1)]
    [InlineData("avatars", 0, "", "GET", true, 1)]
    [InlineData("avatars", 26, "", "GET", true, 1)]
    [InlineData("avatars", 5, "image", "GET", true, 1)]
    [InlineData("avatars", 5, "image/*,<script>", "GET", true, 1)]
    [InlineData("avatars", 5, "", "", true, 1)]
    [InlineData("avatars", 5, "", "", false, 0)]
    public async Task BucketValidation(string name, int maxMegabytes, string types, string methods, bool enabled, int problems)
    {
        var bucket = new Bucket { Id = "b1", Name = name, MaxMegabytes = maxMegabytes, ContentTypes = types, ApiMethods = methods, ApiEnabled = enabled };

        Assert.Equal(problems, (await StorageEndpoints.ProblemsAsync(_db, bucket)).Count);
    }

    [Fact]
    public async Task DuplicateNameRefused()
    {
        _db.Buckets.Add(new Bucket { Id = "b1", Name = "avatars" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await StorageEndpoints.ProblemsAsync(_db, new Bucket { Id = "b2", Name = "avatars" }));
        Assert.Empty(await StorageEndpoints.ProblemsAsync(_db, new Bucket { Id = "b1", Name = "avatars" }));
    }

    [Fact]
    public void BucketChangeIsASchemaChange()
    {
        _db.Buckets.Add(new Bucket { Id = "b3", Name = "logos" });
        Assert.True(RecordChangeInterceptor.SchemaEntriesChanged(_db.ChangeTracker));
    }

    [Theory]
    [InlineData(FileStore.Refusal.TooLarge, 413)]
    [InlineData(FileStore.Refusal.Type, 415)]
    [InlineData(FileStore.Refusal.Full, 507)]
    [InlineData(FileStore.Refusal.Empty, 400)]
    public void RefusalStatus(FileStore.Refusal reason, int status)
    {
        Assert.Equal(status, StorageEndpoints.ProblemFor(reason).Status);
    }

    [Fact]
    public void BucketLimitNarrowsTheInstanceLimit()
    {
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(new byte[2 * 1024 * 1024]), 0, 2 * 1024 * 1024, "file", "a.png");

        Assert.Equal(FileStore.Refusal.TooLarge, FileStore.Check(file, "avatars", 1024 * 1024)?.Reason);
        Assert.NotEqual(FileStore.Refusal.TooLarge, FileStore.Check(file, "avatars", 5L * 1024 * 1024)?.Reason);
    }
}
