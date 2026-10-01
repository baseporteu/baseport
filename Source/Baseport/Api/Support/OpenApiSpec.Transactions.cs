using System.Text.Json.Nodes;

namespace Baseport;

public static partial class OpenApiSpec
{
    public const string TransactionTag = BuiltInTagPrefix + "transactions";

    private const string TransactionRequestSchema = "Transaction.Request";
    private const string TransactionOpSchema = "Transaction.Operation";
    private const string TransactionResultSchema = "Transaction.Result";
    private const string TransactionErrorSchema = "Transaction.Error";

    public static IReadOnlyList<TableDefinition> Writable(IReadOnlyList<TableDefinition> tables) =>
        tables.Where(t => !t.IsProxy && ApiMethods.Parse(t.ApiMethods).Intersect(["POST", "PATCH", "DELETE"]).Any()).ToList();

    private static JsonObject TransactionTagNode() => new()
    {
        ["name"] = TransactionTag,
        ["summary"] = "Transactions",
        ["description"] = $"Up to {RecordTransactions.MaxOperations} writes across tables in one call. Each operation passes the same validation, method switches and access rules as the single-record routes. With `transaction: false` each operation commits on its own; a failure leaves earlier operations standing and reports them in `completed`. With `transaction: true` the batch commits together or not at all."
    };

    private static JsonObject TransactionPaths(bool jwt)
    {
        var failed = new JsonObject
        {
            ["description"] = "",
            ["content"] = new JsonObject { [ApiProblems.ContentType] = new JsonObject { ["schema"] = SchemaRef(TransactionErrorSchema) } }
        };

        JsonObject Failure(string description)
        {
            var response = failed.DeepClone().AsObject();
            response["description"] = description;
            return response;
        }

        var responses = new JsonObject
        {
            ["200"] = JsonResp("Every operation applied", SchemaRef(TransactionResultSchema)),
            ["400"] = Failure("Malformed batch, unknown member, or a proxy table"),
            ["401"] = ProblemResp(ApiProblem.Unauthorized, "Missing or invalid bearer token"),
            ["403"] = Failure("An access rule refused an operation"),
            ["404"] = Failure("Unpublished table or missing record"),
            ["405"] = Failure("Method not enabled for a table or key"),
            ["409"] = Failure("Conflicts with stored data"),
            ["413"] = ProblemResp(ApiProblem.TooLarge, "Body over 4 MB"),
            ["422"] = Failure("A record failed validation")
        };

        return new JsonObject
        {
            ["/api/transaction/v1/execute"] = new JsonObject
            {
                ["post"] = Operation(TransactionTag, "transaction_execute", "Run a batch of writes", Bearer(jwt), responses,
                    JsonBody(TransactionRequestSchema))
            }
        };
    }

    private static JsonObject TransactionSchemas(IReadOnlyList<TableDefinition> writable) => new()
    {
        [TransactionRequestSchema] = Request(["operations"],
            ("operations", new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = RecordTransactions.MaxOperations,
                ["items"] = SchemaRef(TransactionOpSchema)
            }),
            ("transaction", new JsonObject { ["type"] = "boolean", ["default"] = false, ["description"] = "All or nothing when true." })),
        [TransactionOpSchema] = Request(["op", "apiName"],
            ("op", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(RecordTransactions.Kinds.Select(k => (JsonNode)k).ToArray()) }),
            ("apiName", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(writable.Select(t => (JsonNode)t.ApiName).ToArray()) }),
            ("recordId", Str("Required for update and delete.")),
            ("value", new JsonObject
            {
                ["description"] = "Record values for create and update. Update merges.",
                ["oneOf"] = new JsonArray(writable.Select(t => (JsonNode)SchemaRef(SchemaName(t))).ToArray())
            })),
        [TransactionResultSchema] = Closed(("results", new JsonObject
        {
            ["type"] = "array",
            ["description"] = "One entry per operation, in order.",
            ["items"] = Closed(("id", Str("Record id created, updated or deleted.")))
        })),
        [TransactionErrorSchema] = new JsonObject
        {
            ["allOf"] = new JsonArray(
                SchemaRef(ProblemSchema),
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["index"] = new JsonObject { ["type"] = "integer", ["description"] = "0-based position of the operation that failed." },
                        ["completed"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Operations committed before the failure. Always empty when `transaction` is true.",
                            ["items"] = Closed(
                                ("index", new JsonObject { ["type"] = "integer" }),
                                ("id", Str(null)))
                        }
                    }
                })
        }
    };
}
