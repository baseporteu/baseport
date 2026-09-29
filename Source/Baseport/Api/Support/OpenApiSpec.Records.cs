using System.Text.Json.Nodes;

namespace Baseport;

public static partial class OpenApiSpec
{
    public const string NamespacePrefix = "ns:";

    public static JsonArray BuildTags(IReadOnlyList<TableDefinition> tables)
    {
        var namespaces = Namespaces(tables);
        var canonical = namespaces.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var tags = new JsonArray();
        foreach (var ns in namespaces)
            tags.Add(new JsonObject { ["name"] = NamespacePrefix + ns, ["summary"] = ns, ["kind"] = "nav" });

        foreach (var t in tables)
        {
            var tag = new JsonObject
            {
                ["name"] = t.ApiName,
                ["description"] = Documentation(t)
            };
            if (!string.IsNullOrWhiteSpace(t.ApiDisplayName)) tag["summary"] = t.ApiDisplayName.Trim();
            if (NamespaceOf(t) is { } ns) tag["parent"] = NamespacePrefix + canonical[ns];
            tags.Add(tag);
        }
        return tags;
    }

    private static string? NamespaceOf(TableDefinition t) =>
        string.IsNullOrWhiteSpace(t.ApiNamespace) ? null : t.ApiNamespace.Trim();

    private static List<string> Namespaces(IReadOnlyList<TableDefinition> tables) =>
        tables
            .Select(NamespaceOf)
            .OfType<string>()
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Documentation(TableDefinition t)
    {
        if (!string.IsNullOrWhiteSpace(t.ApiDocumentation)) return t.ApiDocumentation;
        if (!string.IsNullOrWhiteSpace(t.Description)) return t.Description;
        return $"Records of {DisplayName(t)}.";
    }

    private static string DisplayName(TableDefinition t) =>
        string.IsNullOrWhiteSpace(t.ApiDisplayName) ? t.ApiName : t.ApiDisplayName;

    private static string SchemaName(TableDefinition t)
    {
        var parts = t.ApiName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static JsonObject BuildPaths(IReadOnlyList<TableDefinition> tables, bool jwt = false)
    {
        var paths = new JsonObject();
        foreach (var t in tables)
        {
            var name = DisplayName(t);
            var allowed = ApiMethods.Parse(t.ApiMethods);

            JsonObject TableRef() => new() { ["$ref"] = $"#/components/schemas/{SchemaName(t)}" };

            var list = new JsonObject();
            if (allowed.Contains("GET"))
                list["get"] = BuildOp(t, jwt, $"list_{SchemaName(t)}", $"List {name} records",
                    Responses(ReadProblems, ("200", JsonResp("OK", PageResponse(TableRef())))),
                    parameters: ListParameters());
            if (allowed.Contains("POST"))
                list["post"] = BuildOp(t, jwt, $"create_{SchemaName(t)}", $"Create a {name} record",
                    Responses(WriteProblems, ("201", Created(TableRef()))),
                    new JsonObject
                    {
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    });

            var item = new JsonObject();
            if (allowed.Contains("GET"))
                item["get"] = BuildOp(t, jwt, $"get_{SchemaName(t)}", $"Get a {name} record",
                    Responses(ReadProblems, ("200", Versioned("OK", TableRef())), ("304", new JsonObject { ["description"] = "Not modified: the `If-None-Match` you sent is the current version." })),
                    parameters: new JsonArray(ExpandParameter(), IfNoneMatchParameter()));
            if (allowed.Contains("PATCH"))
                item["patch"] = BuildOp(t, jwt, $"update_{SchemaName(t)}", $"Update a {name} record",
                    Responses(WriteProblems, ("200", Versioned("Updated", TableRef()))),
                    new JsonObject
                    {
                        ["description"] = "Fields to change. Omitted fields keep their stored value.",
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    },
                    new JsonArray(IfMatchParameter()));
            if (allowed.Contains("PUT"))
                item["put"] = BuildOp(t, jwt, $"replace_{SchemaName(t)}", $"Replace a {name} record",
                    Responses(WriteProblems, ("200", Versioned("Replaced", TableRef()))),
                    new JsonObject
                    {
                        ["description"] = "The full record. Omitted fields are cleared.",
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    },
                    new JsonArray(IfMatchParameter()));
            if (allowed.Contains("DELETE"))
                item["delete"] = BuildOp(t, jwt, $"delete_{SchemaName(t)}", $"Delete a {name} record",
                    Responses(ConditionalProblems, ("200", JsonResp("Deleted", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["deleted"] = new JsonObject { ["type"] = "string" } } }))),
                    parameters: new JsonArray(IfMatchParameter()));

            if (list.Count > 0) paths[$"/api/v1/{t.ApiName}/records"] = list;
            if (item.Count > 0) paths[$"/api/v1/{t.ApiName}/records/{{recordId}}"] = item;

            if (allowed.Contains("GET") && !t.IsProxy)
            {
                paths[$"/api/v1/{t.ApiName}/subscribe"] = new JsonObject
                {
                    ["get"] = BuildOp(t, jwt, $"subscribe_{SchemaName(t)}", $"Stream {name} changes",
                        Responses(StreamProblems, ("200", SseResp(TableRef()))))
                };
                paths[$"/api/v1/{t.ApiName}/subscribe/{{recordId}}"] = new JsonObject
                {
                    ["get"] = BuildOp(t, jwt, $"subscribe_{SchemaName(t)}_record", $"Stream changes to one {name} record",
                        Responses(StreamProblems, ("200", SseResp(TableRef())), ("404", JsonResp("Record not found", ErrorResponse()))))
                };
            }
        }
        return paths;
    }

    public static JsonObject BuildSchemas(IReadOnlyList<TableDefinition> tables)
    {
        var schemas = new JsonObject();
        foreach (var t in tables)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var f in t.Fields)
            {
                if (FieldValidation.NormalizeType(f.DataType) is "calculated" or "derived" or "systemid") continue;
                var ps = FieldSchema(f);
                if (ps == null) continue;
                props[f.Name] = ps;
                if (f.IsRequired && !f.IsHidden) required.Add(f.Name);
            }
            schemas[SchemaName(t)] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = t.Description,
                ["properties"] = props,
                ["required"] = required
            };
        }

        schemas[ProblemSchema] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "RFC 9457 problem details, served as `application/problem+json`. `errors` and `invalid` are extension members: `errors` lists every message, and on a 422 `invalid` names the fields they belong to.",
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string", ["description"] = "Stable identifier for this kind of problem." },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["status"] = new JsonObject { ["type"] = "integer" },
                ["detail"] = new JsonObject { ["type"] = "string" },
                ["instance"] = new JsonObject { ["type"] = "string", ["description"] = "Path of the request that produced it. The query string is deliberately omitted: it can carry a token." },
                ["errors"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["invalid"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            },
            ["required"] = new JsonArray((JsonNode)"type", (JsonNode)"title", (JsonNode)"status", (JsonNode)"errors")
        };
        return schemas;
    }

    private static JsonObject FieldSchema(FieldDefinition f)
    {
        var type = FieldTypes.Of(f);
        var schema = new JsonObject { ["type"] = type.JsonType };
        if (type.JsonFormat is { } fmt) schema["format"] = fmt;

        var options = FieldValidation.ParseOptions(f.OptionsJson);
        var enums = options.Count > 0 && type.Name is "select" or "multiselect"
            ? new JsonArray(options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray())
            : null;

        if (type.Shape == FieldShape.Object) schema = ObjectSchema(f, schema);
        else if (type.Shape == FieldShape.Array) schema["items"] = ItemSchema(f, enums);
        else if (enums is not null) schema["enum"] = enums;

        if (!string.IsNullOrWhiteSpace(f.Pattern)) schema["pattern"] = f.Pattern;
        if (!string.IsNullOrWhiteSpace(f.Label)) schema["title"] = f.Label;
        if (!string.IsNullOrWhiteSpace(f.HelpText)) schema["description"] = f.HelpText;

        if (type.JsonType == "number")
        {
            if (f.Min is { } lo) schema["minimum"] = lo;
            if (f.Max is { } hi) schema["maximum"] = hi;
        }
        else if (type.JsonType == "string" && type.Name is "text" or "longtext")
        {
            if (f.Min is { } lo) schema["minLength"] = (int)lo;
            if (f.Max is { } hi) schema["maxLength"] = (int)hi;
        }
        else if (type.Name == "array" && f.Max is { } max) schema["maxItems"] = (int)max;

        if (!string.IsNullOrEmpty(f.DefaultValue)) schema["default"] = JsonValue.Create(f.DefaultValue);
        if (f.IsUnique && !f.IsHidden) schema["x-unique"] = true;
        return schema;
    }

    private static JsonObject ObjectSchema(FieldDefinition f, JsonObject schema)
    {
        var members = FieldValidation.NestedFields(f.OptionsJson);
        if (members.Count == 0)
        {
            schema["additionalProperties"] = true;
            return schema;
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var m in members)
        {
            properties[m.Name] = FieldSchema(m);
            if (m.IsRequired) required.Add((JsonNode)m.Name);
        }
        schema["properties"] = properties;
        if (required.Count > 0) schema["required"] = required;
        schema["additionalProperties"] = false;
        return schema;
    }

    private static JsonNode ItemSchema(FieldDefinition f, JsonArray? enums)
    {
        if (enums is not null) return new JsonObject { ["type"] = "string", ["enum"] = enums };

        var members = FieldValidation.NestedFields(f.OptionsJson);
        if (members.Count == 0) return new JsonObject();
        return ObjectSchema(f, new JsonObject { ["type"] = "object" });
    }

    private static JsonArray ListParameters() => new(
        Param("q", "Search query string used to filter results across indexed fields.", "string"),
        Param("sort", "Field name to sort results by. Defaults to creation date.", "string"),
        Param("order", "Sort direction: `asc` (ascending) or `desc` (descending). Defaults to `desc`.", "string"),
        Param("page", "1-based page number to retrieve. Defaults to 1.", "integer"),
        Param("pageSize", $"Number of items per page (1 to {QueryEngine.MaxPageSize}). Defaults to 50.", "integer"),
        Param("cursor", "Opaque position from a previous response's `nextCursor`, for keyset paging. A deep page costs the same as the first one and rows inserted mid-walk cannot shift the window. Cannot be combined with `sort`.", "string"),
        FilterParameter(),
        ExpandParameter());

    private static JsonNode FilterParameter() => new JsonObject
    {
        ["name"] = "filter",
        ["in"] = "query",
        ["description"] = "Equality filter on a field, as field:value. Repeat the parameter to AND multiple filters, e.g. filter=OrderId:abc123.",
        ["schema"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
    };

    private static JsonNode ExpandParameter() => Param(ApiLinks.ExpandParameter,
        "List of relation fields to embed under `expanded`. Nested/deep expansion is not supported (1 level deep).", "string");

    private static JsonObject PageResponse(JsonObject dataSchema) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["rows"] = new JsonObject { ["type"] = "array", ["items"] = RecordResponse(dataSchema) },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["pageSize"] = new JsonObject { ["type"] = "integer" },
            ["total"] = new JsonObject { ["type"] = "integer" },
            ["totalPages"] = new JsonObject { ["type"] = "integer" },
            ["hasMore"] = new JsonObject { ["type"] = "boolean" },
            ["countExact"] = new JsonObject { ["type"] = "boolean", ["description"] = $"False past {QueryEngine.CountCeiling} matches: total is a floor." },
            ["nextCursor"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "Pass back as `cursor` for the next page. Null on the last page, and on any listing that named a `sort` field." },
            ["links"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "self, first, and prev/next/last where they exist.",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" }
            }
        }
    };

    private static JsonObject BuildOp(TableDefinition table, bool jwt, string operationId, string summary, JsonObject responses, JsonObject? requestBody = null, JsonArray? parameters = null)
    {
        var op = new JsonObject
        {
            ["operationId"] = operationId,
            ["summary"] = summary,

            ["tags"] = new JsonArray((JsonNode)table.ApiName),
            ["security"] = Bearer(jwt),
            ["responses"] = responses
        };
        if (requestBody != null) op["requestBody"] = requestBody;
        if (parameters != null) op["parameters"] = parameters;
        return op;
    }

    private static JsonObject ETagHeader() => Header("ETag", "Version of the record as returned. Send it back as `If-Match` on a write, or as `If-None-Match` on a read.");

    private static JsonObject Versioned(string description, JsonObject dataSchema)
    {
        var response = JsonResp(description, RecordResponse(dataSchema));
        response["headers"] = ETagHeader();
        return response;
    }

    private static JsonObject Created(JsonObject dataSchema)
    {
        var response = JsonResp("Created", RecordResponse(dataSchema));
        var headers = ETagHeader();
        headers["Location"] = new JsonObject
        {
            ["description"] = "Address of the record that was created.",
            ["schema"] = new JsonObject { ["type"] = "string" }
        };
        response["headers"] = headers;
        return response;
    }

    private static JsonNode IfMatchParameter() => HeaderParam("If-Match",
        "The `ETag` of the version you read. The write is refused with 412 if the record changed since. Omit it to write unconditionally.");

    private static JsonNode IfNoneMatchParameter() => HeaderParam("If-None-Match",
        "The `ETag` you already hold. Answers 304 with no body when it is still current.");

    private static JsonObject Responses(params (string Code, JsonObject Response)[] success) =>
        Responses(Array.Empty<ApiProblem>(), success);

    private static JsonObject Responses(IReadOnlyList<ApiProblem> extra, params (string Code, JsonObject Response)[] success)
    {
        var responses = new JsonObject();
        foreach (var (code, resp) in success) responses[code] = resp;

        foreach (var problem in extra)
            responses[problem.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ProblemResp(problem, problem.Title);

        responses["401"] = ProblemResp(ApiProblem.Unauthorized, "Missing or invalid bearer token");
        responses["403"] = ProblemResp(ApiProblem.Forbidden, "An access rule on this table refused the request");
        responses["404"] = ProblemResp(ApiProblem.NotFound, "Table or record not found");
        responses["406"] = ProblemResp(ApiProblem.NotAcceptable, "The Accept header asks for a format this API does not produce");
        responses["429"] = ProblemResp(ApiProblem.TooManyRequests, "Rate limit exceeded");
        responses["500"] = ProblemResp(ApiProblem.Internal, "Internal server error");
        return responses;
    }

    private static readonly ApiProblem[] ReadProblems = [ApiProblem.BadRequest];

    private static readonly ApiProblem[] WriteProblems =
    [
        ApiProblem.BadRequest, ApiProblem.Conflict, ApiProblem.PreconditionFailed, ApiProblem.TooLarge,
        ApiProblem.UnsupportedMediaType, ApiProblem.Unprocessable
    ];

    private static readonly ApiProblem[] StreamProblems = [ApiProblem.ServiceUnavailable];

    private static readonly ApiProblem[] ConditionalProblems = [ApiProblem.PreconditionFailed];

    public const string StreamEvent = "record";

    private static JsonObject SseResp(JsonObject dataSchema) => new()
    {
        ["description"] = "An open stream. One event per record change until the client disconnects.",
        ["content"] = new JsonObject
        {
            ["text/event-stream"] = new JsonObject
            {
                ["itemSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "One Server-Sent Event per change to one record.",
                    ["properties"] = new JsonObject
                    {
                        ["event"] = new JsonObject { ["type"] = "string", ["const"] = StreamEvent },
                        ["data"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["contentMediaType"] = "application/json",
                            ["contentSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["action"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new JsonArray("create", "update", "delete"),
                                        ["description"] = "What happened to the record."
                                    },
                                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Unguessable record identifier." },
                                    ["record"] = new JsonObject
                                    {
                                        ["description"] = "The stored values after the change; null for a delete, whose row is gone.",
                                        ["anyOf"] = new JsonArray(dataSchema, new JsonObject { ["type"] = "null" })
                                    }
                                },
                                ["required"] = new JsonArray("action", "id", "record")
                            }
                        }
                    },
                    ["required"] = new JsonArray("event", "data")
                }
            }
        }
    };

    private static JsonObject RecordResponse(JsonObject dataSchema) => new()
    {
        ["type"] = "object",
        ["description"] = "One record: its identifier, when it was created and last changed, and the stored values.",

        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Unguessable record identifier." },
            ["createdAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["updatedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Equal to createdAt until the record is first changed." },
            ["data"] = dataSchema,
            ["links"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "self, collection, and one link per reference field.",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" }
            },
            ["expanded"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = $"Records named by {ApiLinks.ExpandParameter}, keyed by field.",
                ["additionalProperties"] = new JsonObject { ["type"] = "object" }
            }
        },
        ["required"] = new JsonArray((JsonNode)"id", (JsonNode)"createdAt", (JsonNode)"updatedAt", (JsonNode)"data", (JsonNode)"links")
    };
}
