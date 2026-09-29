using System.Text.Json.Nodes;

namespace Baseport;

public static partial class OpenApiSpec
{
    public const string StorageTag = BuiltInTagPrefix + "storage";

    private const string StoredFileSchema = "Storage.File";
    private const string FileDeletedSchema = "Storage.Deleted";

    public static string BucketTag(Bucket bucket) => $"{BuiltInTagPrefix}bucket:{bucket.Name}";

    private static JsonObject StorageTagNode() => new()
    {
        ["name"] = StorageTag,
        ["summary"] = "Storage",
        ["kind"] = "nav",
        ["description"] = "File buckets. A stored file is also served anonymously at its `url`; the random name is the credential."
    };

    private static JsonObject BucketTagNode(Bucket bucket) => new()
    {
        ["name"] = BucketTag(bucket),
        ["summary"] = bucket.Name,
        ["parent"] = StorageTag,
        ["description"] = string.IsNullOrWhiteSpace(bucket.Description) ? $"Files in the `{bucket.Name}` bucket." : bucket.Description
    };

    private static JsonObject StoragePaths(IReadOnlyList<Bucket> buckets, bool jwt)
    {
        var paths = new JsonObject();
        foreach (var bucket in buckets)
        {
            var methods = BucketMethods.Parse(bucket.ApiMethods);
            var endUsers = jwt && bucket.AllowJwt;
            var tag = BucketTag(bucket);
            var denied = new List<(ApiProblem, string)>
            {
                (ApiProblem.Unauthorized, "Missing or invalid bearer token"),
                (ApiProblem.NotFound, "Bucket or file not found"),
                (ApiProblem.MethodNotAllowed, "Method not enabled for this bucket or key")
            };
            if (jwt && !bucket.AllowJwt) denied.Add((ApiProblem.Forbidden, "End-user tokens are not accepted by this bucket"));

            if (methods.Contains("POST"))
                paths[$"/api/v1/files/{bucket.Name}"] = new JsonObject
                {
                    ["post"] = Operation(tag, $"files_upload_{bucket.Name}", $"Upload to {bucket.Name}", Bearer(endUsers),
                        Outcomes([("201", StoredResponse())],
                        [
                            (ApiProblem.BadRequest, "No file part, or an empty file"),
                            .. denied,
                            (ApiProblem.TooLarge, $"File over {bucket.MaxMegabytes} MB"),
                            (ApiProblem.UnsupportedMediaType, "Not multipart/form-data, or a file type this bucket refuses"),
                            (ApiProblem.TooManyRequests, "Rate limit exceeded"),
                            (ApiProblem.InsufficientStorage, "Instance upload storage full or disk low")
                        ]),
                        UploadBody(bucket),
                        description: $"Files up to {bucket.MaxMegabytes} MB." + (BucketMethods.ContentTypes(bucket) is { Count: > 0 } types ? $" Accepted types: {string.Join(", ", types)}." : ""))
                };

            var item = new JsonObject();
            var fileParameter = new JsonArray(new JsonObject
            {
                ["name"] = "file",
                ["in"] = "path",
                ["required"] = true,
                ["description"] = "The `name` returned by the upload.",
                ["schema"] = new JsonObject { ["type"] = "string" }
            });
            if (methods.Contains("GET"))
                item["get"] = Operation(tag, $"files_get_{bucket.Name}", $"Download from {bucket.Name}", Bearer(endUsers),
                    Outcomes(
                    [
                        ("200", new JsonObject
                        {
                            ["description"] = "File content",
                            ["headers"] = Header("Accept-Ranges", "`bytes`: ranged requests are answered with 206."),
                            ["content"] = new JsonObject { ["*/*"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["contentMediaType"] = "application/octet-stream" } } }
                        }),
                        ("206", new JsonObject { ["description"] = "Requested byte range" })
                    ], denied),
                    parameters: fileParameter.DeepClone().AsArray());
            if (methods.Contains("DELETE"))
                item["delete"] = Operation(tag, $"files_delete_{bucket.Name}", $"Delete from {bucket.Name}", Bearer(endUsers),
                    Outcomes([("200", JsonResp("Deleted", SchemaRef(FileDeletedSchema)))], denied),
                    parameters: fileParameter.DeepClone().AsArray());
            if (item.Count > 0) paths[$"/api/v1/files/{bucket.Name}/{{file}}"] = item;
        }
        return paths;
    }

    private static JsonObject StoredResponse()
    {
        var response = JsonResp("Stored", SchemaRef(StoredFileSchema));
        response["headers"] = Header("Location", "API address of the stored file.");
        return response;
    }

    private static JsonObject UploadBody(Bucket bucket)
    {
        var media = new JsonObject
        {
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["file"] = new JsonObject { ["type"] = "string", ["format"] = "binary", ["contentMediaType"] = "application/octet-stream" }
                },
                ["required"] = new JsonArray("file")
            }
        };
        if (BucketMethods.ContentTypes(bucket) is { Count: > 0 } types)
            media["encoding"] = new JsonObject { ["file"] = new JsonObject { ["contentType"] = string.Join(", ", types) } };

        return new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject { ["multipart/form-data"] = media }
        };
    }

    private static JsonObject StorageSchemas() => new()
    {
        [StoredFileSchema] = Closed(
            ("id", Str("Bucket and name, as stored.")),
            ("bucket", Str(null)),
            ("name", Str("Random file name. Use it in the download and delete routes.")),
            ("url", new JsonObject { ["type"] = "string", ["format"] = "uri", ["description"] = "Anonymous download address. Anyone holding it can read the file." }),
            ("size", new JsonObject { ["type"] = "integer", ["format"] = "int64" }),
            ("content_type", Str("Derived from the file extension."))),
        [FileDeletedSchema] = Closed(("deleted", Str("Bucket and name of the removed file.")))
    };
}
