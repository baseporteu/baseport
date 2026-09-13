using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class HtmlDisplayValueTests
{
    [Fact]
    public void An_object_cell_reads_as_typed_not_as_escape_sequences()
    {
        var node = JsonNode.Parse("""{"Phone":"+31 6 1234 5678","Note":"a & b"}""");

        var shown = Html.DisplayValue(node);

        Assert.Contains("+31 6 1234 5678", shown);
        Assert.Contains("a & b", shown);
        Assert.DoesNotContain("\\u", shown);
    }

    [Fact]
    public void A_list_of_objects_reads_the_same_way()
    {
        var node = JsonNode.Parse("""[{"Phone":"+31 6 1234 5678"}]""");
        Assert.DoesNotContain("\\u", Html.DisplayValue(node));
    }

    [Fact]
    public void A_cell_still_escapes_markup_in_the_value()
    {
        var node = JsonNode.Parse("""{"X":"<script>alert(1)</script>"}""");

        var cell = Html.Cell(Html.DisplayValue(node));

        Assert.DoesNotContain("<script>", cell);
        Assert.Contains("&lt;script&gt;", cell);
    }
}
