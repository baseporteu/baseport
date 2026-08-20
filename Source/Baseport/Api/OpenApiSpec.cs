using System.Text.Json.Nodes;

namespace Baseport;

public static class OpenApiSpec
{
    // One tag per exposed table, so the document is navigable in any spec viewer.
    public static JsonArray BuildTags(List<TableDefinition> tables) =>
        new(tables.Select(t =>
        {
            var tag = new JsonObject
            {
                ["name"] = t.ApiName,
                ["description"] = Documentation(t)
            };
            // Renderers show this instead of the route-shaped tag name.
            if (!string.IsNullOrWhiteSpace(t.ApiDisplayName)) tag["x-displayName"] = t.ApiDisplayName;
            return (JsonNode)tag;
        }).ToArray());

    // The author's markdown, or a sentence when they wrote none.
    private static string Documentation(TableDefinition t)
    {
        if (!string.IsNullOrWhiteSpace(t.ApiDocumentation)) return t.ApiDocumentation;
        if (!string.IsNullOrWhiteSpace(t.Description)) return t.Description;
        return $"Records of {DisplayName(t)}.";
    }

    private static string DisplayName(TableDefinition t) =>
        string.IsNullOrWhiteSpace(t.ApiDisplayName) ? t.ApiName : t.ApiDisplayName;

    // Namespaces, as the tag groups a reference renders into sidebar sections.
    public static JsonArray? BuildTagGroups(List<TableDefinition> tables)
    {
        var grouped = tables
            .Where(t => !string.IsNullOrWhiteSpace(t.ApiNamespace))
            .GroupBy(t => t.ApiNamespace.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (grouped.Count == 0) return null;

        var groups = new JsonArray();
        foreach (var group in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            groups.Add(new JsonObject
            {
                ["name"] = group.Key,
                ["tags"] = new JsonArray(group.Select(t => (JsonNode)t.ApiName).ToArray())
            });

        // Anything without a namespace still has to appear, or publishing a second table would make the first vanish from the sidebar.
        var loose = tables.Where(t => string.IsNullOrWhiteSpace(t.ApiNamespace)).ToList();
        if (loose.Count > 0)
            groups.Add(new JsonObject
            {
                ["name"] = "Other",
                ["tags"] = new JsonArray(loose.Select(t => (JsonNode)t.ApiName).ToArray())
            });

        return groups;
    }

    // The schema name a consumer sees.
    private static string SchemaName(TableDefinition t)
    {
        var parts = t.ApiName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static JsonObject BuildPaths(List<TableDefinition> tables)
    {
        var paths = new JsonObject();
        foreach (var t in tables)
        {
            var name = DisplayName(t);
            var allowed = ApiMethods.Parse(t.ApiMethods);
            // A JsonNode may have only one parent, and this schema is referenced by create, update and replace, so each use needs its own instance.
            JsonObject TableRef() => new() { ["$ref"] = $"#/components/schemas/{SchemaName(t)}" };

            var list = new JsonObject();
            if (allowed.Contains("GET"))
                list["get"] = BuildOp(t, $"list_{SchemaName(t)}", $"List {name} records",
                    Responses(("200", JsonResp("OK", PageResponse()))),
                    parameters: ListParameters());
            if (allowed.Contains("POST"))
                list["post"] = BuildOp(t, $"create_{SchemaName(t)}", $"Create a {name} record",
                    Responses(
                        ("201", JsonResp("Created", RecordResponse())),
                        ("400", JsonResp("Validation error", ErrorResponse()))),
                    new JsonObject
                    {
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    });

            var item = new JsonObject();
            if (allowed.Contains("GET"))
                item["get"] = BuildOp(t, $"get_{SchemaName(t)}", $"Get a {name} record",
                    Responses(("200", JsonResp("OK", RecordResponse()))),
                    parameters: new JsonArray(ExpandParameter()));
            if (allowed.Contains("PATCH"))
                item["patch"] = BuildOp(t, $"update_{SchemaName(t)}", $"Update a {name} record",
                    Responses(
                        ("200", JsonResp("Updated", RecordResponse())),
                        ("400", JsonResp("Validation error", ErrorResponse()))),
                    new JsonObject
                    {
                        ["description"] = "Fields to change. Omitted fields keep their stored value.",
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    });
            if (allowed.Contains("PUT"))
                item["put"] = BuildOp(t, $"replace_{SchemaName(t)}", $"Replace a {name} record",
                    Responses(
                        ("200", JsonResp("Replaced", RecordResponse())),
                        ("400", JsonResp("Validation error", ErrorResponse()))),
                    new JsonObject
                    {
                        ["description"] = "The full record. Omitted fields are cleared.",
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = TableRef() } }
                    });
            if (allowed.Contains("DELETE"))
                item["delete"] = BuildOp(t, $"delete_{SchemaName(t)}", $"Delete a {name} record",
                    Responses(("200", JsonResp("Deleted", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["deleted"] = new JsonObject { ["type"] = "string" } } }))));

            // A path with no operations left is not an empty path, it is no path.
            if (list.Count > 0) paths[$"/api/v1/{t.ApiName}/records"] = list;
            if (item.Count > 0) paths[$"/api/v1/{t.ApiName}/records/{{recordId}}"] = item;

            // The stream is a read, so it rides on the GET switch.
            if (allowed.Contains("GET") && !t.IsProxy)
            {
                paths[$"/api/v1/{t.ApiName}/subscribe"] = new JsonObject
                {
                    ["get"] = BuildOp(t, $"subscribe_{SchemaName(t)}", $"Stream {name} changes",
                        Responses(("200", SseResp())))
                };
                paths[$"/api/v1/{t.ApiName}/subscribe/{{recordId}}"] = new JsonObject
                {
                    ["get"] = BuildOp(t, $"subscribe_{SchemaName(t)}_record", $"Stream changes to one {name} record",
                        Responses(("200", SseResp()), ("404", JsonResp("Record not found", ErrorResponse()))))
                };
            }
        }
        return paths;
    }

    public static JsonObject BuildSchemas(List<TableDefinition> tables)
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
        // Every non-2xx response the API returns speaks this one shape, so one registered schema keeps the error contract consistent and referenceable.
        schemas["Error"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Every error response, whatever its status code. On a 400 validation failure, `invalid` names the storage field each error belongs to, so a client can paint exactly the offending inputs.",
            ["properties"] = new JsonObject
            {
                ["errors"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["invalid"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            },
            ["required"] = new JsonArray((JsonNode)"errors")
        };
        return schemas;
    }

    private static JsonObject FieldSchema(FieldDefinition f)
    {
        var t = FieldValidation.NormalizeType(f.DataType);
        JsonObject schema = t switch
        {
            "number" or "currency" => new JsonObject { ["type"] = "number" },
            "boolean" => new JsonObject { ["type"] = "boolean" },
            _ => new JsonObject { ["type"] = "string" }
        };
        if (t is "select" or "multiselect")
        {
            var opts = FieldValidation.ParseOptions(f.OptionsJson);
            if (opts.Count > 0) schema["enum"] = new JsonArray(opts.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray());
        }
        if (!string.IsNullOrWhiteSpace(f.Pattern)) schema["pattern"] = f.Pattern;
        if (!string.IsNullOrWhiteSpace(f.Label)) schema["title"] = f.Label;
        if (!string.IsNullOrWhiteSpace(f.HelpText)) schema["description"] = f.HelpText;

        // Min/Max are value bounds on numerics and length bounds on text, the same two columns, so map them to whichever JSON Schema keyword applies.
        if (t is "number" or "currency")
        {
            if (f.Min is { } lo) schema["minimum"] = lo;
            if (f.Max is { } hi) schema["maximum"] = hi;
        }
        else if (t is "text" or "longtext")
        {
            if (f.Min is { } lo) schema["minLength"] = (int)lo;
            if (f.Max is { } hi) schema["maxLength"] = (int)hi;
        }
        else if (t is "date") schema["format"] = "date";
        else if (t is "datetime") schema["format"] = "date-time";
        else if (t is "file") schema["format"] = "uri";

        if (!string.IsNullOrEmpty(f.DefaultValue)) schema["default"] = JsonValue.Create(f.DefaultValue);
        return schema;
    }

    // Query parameters the paged list operation accepts.
    private static JsonArray ListParameters() => new(
        Param("q", "Search query string used to filter results across indexed fields.", "string"),
        Param("sort", "Field name to sort results by. Defaults to creation date.", "string"),
        Param("order", "Sort direction: `asc` (ascending) or `desc` (descending). Defaults to `desc`.", "string"),
        Param("page", "1-based page number to retrieve. Defaults to 1.", "integer"),
        Param("pageSize", $"Number of items per page (1 to {QueryEngine.MaxPageSize}). Defaults to 50.", "integer"),
        ExpandParameter());

    private static JsonNode ExpandParameter() => Param(ApiLinks.ExpandParameter,
        "List of relation fields to embed under `expanded`. Nested/deep expansion is not supported (1 level deep).", "string");

    private static JsonNode Param(string name, string description, string type) => new JsonObject
    {
        ["name"] = name,
        ["in"] = "query",
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = type }
    };

    private static JsonObject PageResponse() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["rows"] = new JsonObject { ["type"] = "array", ["items"] = RecordResponse() },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["pageSize"] = new JsonObject { ["type"] = "integer" },
            ["total"] = new JsonObject { ["type"] = "integer" },
            ["totalPages"] = new JsonObject { ["type"] = "integer" },
            ["hasMore"] = new JsonObject { ["type"] = "boolean" },
            ["countExact"] = new JsonObject { ["type"] = "boolean", ["description"] = $"False past {QueryEngine.CountCeiling} matches: total is a floor." },
            ["links"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "self, first, and prev/next/last where they exist.",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" }
            }
        }
    };

    private static JsonObject BuildOp(TableDefinition table, string operationId, string summary, JsonObject responses, JsonObject? requestBody = null, JsonArray? parameters = null)
    {
        var op = new JsonObject
        {
            ["operationId"] = operationId,
            ["summary"] = summary,
            // One tag per table so generated clients group by object, not into a single "records" bucket that grows unusable past a few tables.
            ["tags"] = new JsonArray((JsonNode)table.ApiName),
            ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
            ["responses"] = responses
        };
        if (requestBody != null) op["requestBody"] = requestBody;
        if (parameters != null) op["parameters"] = parameters;
        return op;
    }

    private static JsonObject JsonResp(string description, JsonObject? schema)
    {
        if (schema == null) return new JsonObject { ["description"] = description };
        return new JsonObject
        {
            ["description"] = description,
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } }
        };
    }

    // Merges an operation's success responses with the standard error set that applies to every public operation: 401 missing or invalid token, 404 unknown table or record, and a 500 fallback for the unexpected.
    private static JsonObject Responses(params (string Code, JsonObject Response)[] success)
    {
        var responses = new JsonObject();
        foreach (var (code, resp) in success) responses[code] = resp;
        responses["401"] = JsonResp("Missing or invalid bearer token", ErrorResponse());
        responses["404"] = JsonResp("Table or record not found", ErrorResponse());
        responses["500"] = JsonResp("Internal server error", ErrorResponse());
        return responses;
    }

    // OpenAPI 3.2's itemSchema: the response is an unbounded text/event-stream, so what is described is one event, not the body.
    private static JsonObject SseResp() => new()
    {
        ["description"] = "An open stream. One event per record change until the client disconnects.",
        ["content"] = new JsonObject
        {
            ["text/event-stream"] = new JsonObject
            {
                ["itemSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "One change to one record.",
                    // Must match the shape PublicApiEndpoints.Stream yields.
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
                            ["type"] = "object",
                            ["nullable"] = true,
                            ["description"] = "The stored values after the change; null for a delete, whose row is gone."
                        }
                    },
                    ["required"] = new JsonArray("action", "id")
                }
            }
        }
    };

    private static JsonObject RecordResponse() => new()
    {
        ["type"] = "object",
        ["description"] = "One record: its identifier, when it was created and last changed, and the stored values.",
        // These names must match ApiDtos.RecordDto exactly.
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Unguessable record identifier." },
            ["createdAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["updatedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Equal to createdAt until the record is first changed." },
            ["data"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
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

    // Reference to the shared Error schema, the one shape every non-2xx response speaks.
    private static JsonObject ErrorResponse() => new()
    {
        ["$ref"] = "#/components/schemas/Error"
    };
}

