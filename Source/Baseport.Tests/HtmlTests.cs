using Xunit;
using Baseport;

namespace Baseport.Tests;

// The delete confirmation names the row it is about, and that name is record data going into an onclick attribute.
public class HtmlTests
{
    [Fact]
    public void A_delete_button_transports_the_identifier_alongside_the_id()
    {
        var html = Html.Button("Delete", "deleteRecord", "abc123", "ACME-001");

        Assert.Contains("deleteRecord(&#39;abc123&#39;, &#39;ACME-001&#39;)", html);
    }

    [Fact]
    public void An_identifier_cannot_break_out_of_the_onclick_it_sits_in()
    {
        var html = Html.Button("Delete", "deleteRecord", "abc123", "'); alert(1); //");

        // What the browser hands the JS parser once the entities are decoded: the injected quote is escaped, the payload never leaves the string literal.
        var decoded = html.Replace("&#39;", "'").Replace("&quot;", "\"");
        Assert.Contains("""deleteRecord('abc123', '\'); alert(1); //')""", decoded);
    }

    [Fact]
    public void A_long_identifier_is_cut_so_the_dialog_still_fits()
    {
        Assert.Equal("short", Html.Shorten("short"));
        Assert.Equal(new string('x', 60), Html.Shorten(new string('x', 60)));
        Assert.Equal(new string('x', 60) + "…", Html.Shorten(new string('x', 61)));
        Assert.Equal("cut here…", Html.Shorten("cut here      and more", 14));
    }
}
