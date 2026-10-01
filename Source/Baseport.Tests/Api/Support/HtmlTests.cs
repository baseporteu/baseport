using Xunit;
using Baseport;

namespace Baseport.Tests;

public class HtmlTests
{
    [Fact]
    public void DeleteButtonCarriesIdentifier()
    {
        var html = Html.Button("Delete", "deleteRecord", "abc123", "ACME-001");

        Assert.Contains("deleteRecord(&#39;abc123&#39;, &#39;ACME-001&#39;)", html);
    }

    [Fact]
    public void IdentifierCannotEscapeOnclick()
    {
        var html = Html.Button("Delete", "deleteRecord", "abc123", "'); alert(1); //");

        var decoded = html.Replace("&#39;", "'").Replace("&quot;", "\"");
        Assert.Contains("""deleteRecord('abc123', '\'); alert(1); //')""", decoded);
    }

    [Fact]
    public void LongIdentifierIsCut()
    {
        Assert.Equal("short", Html.Shorten("short"));
        Assert.Equal(new string('x', 60), Html.Shorten(new string('x', 60)));
        Assert.Equal(new string('x', 60) + "…", Html.Shorten(new string('x', 61)));
        Assert.Equal("cut here…", Html.Shorten("cut here      and more", 14));
    }
}
