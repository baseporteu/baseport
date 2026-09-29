using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class OpenApiTransactionTests
{
    private static TableDefinition Table(string apiName, string methods, bool proxy = false) => new()
    {
        Id = Ids.NewShortId(12),
        Name = apiName,
        ApiName = apiName,
        ApiEnabled = true,
        ApiMethods = methods,
        IsProxy = proxy,
        Fields = [new() { Id = Ids.NewShortId(12), TableId = "x", Name = "body", DataType = "text" }]
    };

    private static JsonObject Doc(params TableDefinition[] tables) =>
        OpenApiSpec.BuildDocument(new DocumentInputs(tables, new AppSettings(), "1", [], []));

    private static JsonNode? Execute(JsonObject doc) => doc["paths"]!["/api/transaction/v1/execute"]?["post"];

    [Fact]
    public void AbsentWithoutAWritableTable()
    {
        var doc = Doc(Table("reports", "GET"), Table("remote", "GET,POST", proxy: true));

        Assert.Null(Execute(doc));
        Assert.DoesNotContain(OpenApiSpec.TransactionTag, doc.ToJsonString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void PresentWithAnyWriteVerb(string verb)
    {
        Assert.NotNull(Execute(Doc(Table("notes", "GET," + verb))));
    }

    [Fact]
    public void ApiNameListsWritableTablesOnly()
    {
        var doc = Doc(Table("notes", "GET,POST"), Table("reports", "GET"), Table("remote", "POST", proxy: true));
        var op = doc["components"]!["schemas"]!["Transaction.Operation"]!["properties"]!;

        Assert.Equal(new[] { "notes" }, op["apiName"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(RecordTransactions.Kinds, op["op"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("#/components/schemas/Notes", op["value"]!["oneOf"]![0]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public void BatchCapIsDocumented()
    {
        var operations = Doc(Table("notes", "POST"))["components"]!["schemas"]!["Transaction.Request"]!["properties"]!["operations"]!;

        Assert.Equal(RecordTransactions.MaxOperations, operations["maxItems"]!.GetValue<int>());
        Assert.Equal(1, operations["minItems"]!.GetValue<int>());
    }

    [Fact]
    public void FailuresDescribeCompletedWork()
    {
        var doc = Doc(Table("notes", "POST"));
        var responses = Execute(doc)!["responses"]!;

        foreach (var code in new[] { "400", "403", "404", "405", "409", "422" })
            Assert.Equal("#/components/schemas/Transaction.Error",
                responses[code]!["content"]![ApiProblems.ContentType]!["schema"]!["$ref"]!.GetValue<string>());

        var error = doc["components"]!["schemas"]!["Transaction.Error"]!["allOf"]!.AsArray();
        Assert.Equal($"#/components/schemas/{OpenApiSpec.ProblemSchema}", error[0]!["$ref"]!.GetValue<string>());
        Assert.NotNull(error[1]!["properties"]!["completed"]);
        Assert.NotNull(error[1]!["properties"]!["index"]);
    }

    [Fact]
    public void SchemasMatchDtos()
    {
        var schemas = Doc(Table("notes", "POST"))["components"]!["schemas"]!;
        List<string> Props(string name) => schemas[name]!["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> Keys<T>(T value) => JsonSerializer.SerializeToNode(value)!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(Keys(new TransactionRequest([], true)), Props("Transaction.Request"));
        Assert.Equal(Keys(new TransactionOp("create", "notes", "r", new JsonObject())), Props("Transaction.Operation"));
        Assert.Equal(Keys(new TransactionResultDto([])), Props("Transaction.Result"));
        Assert.Equal(Keys(new TransactionIdDto("x")), schemas["Transaction.Result"]!["properties"]!["results"]!["items"]!["properties"]!.AsObject().Select(p => p.Key).ToList());
    }
}
