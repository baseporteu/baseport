using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class HtmlDisplayValueTests
{
    [Fact]
    public void ObjectCellReadsAsTyped()
    {
        var node = JsonNode.Parse("""{"Phone":"+31 6 1234 5678","Note":"a & b"}""");

        var shown = Html.DisplayValue(node);

        Assert.Contains("+31 6 1234 5678", shown);
        Assert.Contains("a & b", shown);
        Assert.DoesNotContain("\\u", shown);
    }

    [Fact]
    public void ObjectListReadsAsTyped()
    {
        var node = JsonNode.Parse("""[{"Phone":"+31 6 1234 5678"}]""");
        Assert.DoesNotContain("\\u", Html.DisplayValue(node));
    }

    [Fact]
    public void CellEscapesMarkup()
    {
        var node = JsonNode.Parse("""{"X":"<script>alert(1)</script>"}""");

        var cell = Html.Cell(Html.DisplayValue(node));

        Assert.DoesNotContain("<script>", cell);
        Assert.Contains("&lt;script&gt;", cell);
    }
}
