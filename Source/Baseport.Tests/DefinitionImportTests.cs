using Xunit;
using System.Text;
using System.Text.Json.Nodes;
using Baseport;

namespace Baseport.Tests;

// A file import types every column from the file itself, the parser and the inference are the whole feature: a wrong guess here becomes a table the author has to fix by hand.
public class DefinitionImportTests
{
    private static (List<JsonObject> Rows, string? Error) Parse(string text, string name) =>
        DefinitionImport.Parse(Encoding.UTF8.GetBytes(text), name);

    private static string TypeOf(IEnumerable<OpenApiProxy.FieldProp> props, string name) =>
        OpenApiProxy.MapFieldType(props.First(p => p.Name == name));

    [Fact]
    public void Csv_reads_quoted_fields_holding_the_delimiter_a_quote_and_a_newline()
    {
        var (rows, error) = Parse("name,note\n\"Doe, John\",\"He said \"\"hi\"\"\"\n\"two\nlines\",plain\n", "people.csv");
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Doe, John", rows[0]["name"]!.GetValue<string>());
        Assert.Equal("He said \"hi\"", rows[0]["note"]!.GetValue<string>());
        Assert.Equal("two\nlines", rows[1]["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("a;b\n1;2\n")]
    [InlineData("a\tb\n1\t2\n")]
    [InlineData("a|b\n1|2\n")]
    public void Csv_sniffs_the_delimiter_from_the_header(string text)
    {
        var (rows, error) = Parse(text, "data.csv");
        Assert.Null(error);
        Assert.Equal("2", rows[0]["b"]!.GetValue<string>());
    }

    [Fact]
    public void Csv_renames_a_duplicate_header_instead_of_dropping_its_column()
    {
        var (rows, error) = Parse("id,id\n1,2\n", "dupe.csv");
        Assert.Null(error);
        Assert.Equal("1", rows[0]["id"]!.GetValue<string>());
        Assert.Equal("2", rows[0]["id_2"]!.GetValue<string>());
    }

    [Fact]
    public void Csv_with_only_a_header_is_refused_rather_than_imported_empty()
    {
        var (rows, error) = Parse("a,b\n", "empty.csv");
        Assert.Empty(rows);
        Assert.NotNull(error);
    }

    [Fact]
    public void Json_unwraps_a_collection_envelope()
    {
        var (rows, error) = Parse("""{"value":[{"sku":"P1"},{"sku":"P2"}]}""", "products.json");
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Xml_reads_the_largest_set_of_repeating_siblings_as_rows()
    {
        var (rows, error) = Parse(
            "<catalog><meta><v>1</v></meta><item id='1'><sku>P1</sku></item><item id='2'><sku>P2</sku></item></catalog>",
            "catalog.xml");
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
        Assert.Equal("P1", rows[0]["sku"]!.GetValue<string>());
        Assert.Equal("1", rows[0]["id"]!.GetValue<string>());
    }

    [Fact]
    public void A_malformed_file_reports_why_instead_of_importing_nothing_quietly()
    {
        Assert.NotNull(Parse("{not json", "x.json").Error);
        Assert.NotNull(Parse("<a><b></a>", "x.xml").Error);
        Assert.NotNull(DefinitionImport.Parse(Array.Empty<byte>(), "x.csv").Error);
    }

    [Fact]
    public void Over_the_row_cap_is_refused_whole_rather_than_truncated()
    {
        var sb = new StringBuilder("a\n");
        for (var i = 0; i <= DefinitionImport.MaxRows; i++) sb.Append(i).Append('\n');
        var (rows, error) = Parse(sb.ToString(), "big.csv");
        Assert.Empty(rows);
        Assert.Contains("limit", error);
    }

    // Every CSV cell is a string, a column typed off one sample row types the whole file as text.
    [Fact]
    public void A_column_of_strings_is_typed_from_the_whole_column()
    {
        var (rows, _) = Parse(
            "qty,flag,when,stamp,email,site,note\n" +
            "1,true,2024-01-02,2024-01-02T03:04:05Z,a@b.com,https://x.com,hello\n" +
            "2,false,2024-02-03,2024-02-03T03:04:05Z,c@d.com,https://y.com,world there\n",
            "mixed.csv");
        var props = DefinitionImport.InferFields(rows);
        Assert.Equal("number", TypeOf(props, "qty"));
        Assert.Equal("boolean", TypeOf(props, "flag"));
        Assert.Equal("date", TypeOf(props, "when"));
        Assert.Equal("datetime", TypeOf(props, "stamp"));
        Assert.Equal("email", TypeOf(props, "email"));
        Assert.Equal("url", TypeOf(props, "site"));
        Assert.Equal("text", TypeOf(props, "note"));
    }

    [Fact]
    public void One_unparseable_value_drops_the_whole_column_back_to_text()
    {
        var (rows, _) = Parse("qty\n1\n2\nn/a\n", "mixed.csv");
        Assert.Equal("text", TypeOf(DefinitionImport.InferFields(rows), "qty"));
    }

    [Fact]
    public void A_blank_cell_does_not_vote_on_the_column_type()
    {
        var (rows, _) = Parse("qty,note\n1,a\n,b\n3,c\n", "gaps.csv");
        var props = DefinitionImport.InferFields(rows);
        Assert.Equal("number", TypeOf(props, "qty"));
        Assert.False(props.First(p => p.Name == "qty").Required);
    }

    // An all-digit identifier parses as a date too; typing it as one would silently reformat every value.
    [Fact]
    public void A_numeric_identifier_is_a_number_and_not_a_date()
    {
        var (rows, _) = Parse("code\n20240101\n20240102\n", "codes.csv");
        Assert.Equal("number", TypeOf(DefinitionImport.InferFields(rows), "code"));
    }

    [Fact]
    public void A_small_repeating_set_of_values_becomes_a_choice_list()
    {
        var (rows, _) = Parse("status\nopen\nclosed\nopen\nclosed\nopen\nclosed\n", "tickets.csv");
        var prop = DefinitionImport.InferFields(rows).First(p => p.Name == "status");
        Assert.Equal("select", OpenApiProxy.MapFieldType(prop));
        Assert.Equal(new[] { "closed", "open" }, prop.EnumValues);
    }

    // A remote column sampled from a live endpoint must stay open: the three values one page happened to show are not the API's whole enum.
    [Fact]
    public void A_sampled_remote_column_is_never_closed_into_a_choice_list()
    {
        var (rows, _) = Parse("status\nopen\nclosed\nopen\nclosed\nopen\nclosed\n", "tickets.csv");
        var prop = DefinitionImport.InferFields(rows, detectChoices: false).First(p => p.Name == "status");
        Assert.Empty(prop.EnumValues);
        Assert.Equal("text", OpenApiProxy.MapFieldType(prop));
    }

    [Fact]
    public void A_column_present_and_filled_in_every_row_is_required()
    {
        var (rows, _) = Parse("a,b\n1,\n2,x\n", "req.csv");
        var props = DefinitionImport.InferFields(rows);
        Assert.True(props.First(p => p.Name == "a").Required);
        Assert.False(props.First(p => p.Name == "b").Required);
    }

    [Theory]
    [InlineData("First Name", "First_Name")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("a--b", "a_b")]
    [InlineData("$$$", "column3")]
    [InlineData("123", "column3")]
    public void A_header_becomes_a_field_name_the_validator_accepts(string header, string expected)
    {
        Assert.Equal(expected, DefinitionImport.SafeName(header, 3));
    }

    [Fact]
    public void A_row_maps_onto_a_field_by_name_by_label_or_by_the_sanitized_header()
    {
        var fields = new List<FieldDefinition>
        {
            new() { Name = "First_Name", Label = "First Name" },
            new() { Name = "qty" },
            new() { Name = "absent" }
        };
        var row = new JsonObject { ["First Name"] = "Ada", ["QTY"] = "3", ["ignored"] = "x" };
        var mapped = DefinitionImport.MapRow(row, fields);

        Assert.Equal("Ada", mapped["First_Name"]!.GetValue<string>());
        Assert.Equal("3", mapped["qty"]!.GetValue<string>());
        Assert.False(mapped.ContainsKey("absent"));
        Assert.False(mapped.ContainsKey("ignored"));
    }

    [Fact]
    public void A_blank_cell_is_left_out_so_a_field_default_still_applies()
    {
        var fields = new List<FieldDefinition> { new() { Name = "note" } };
        var mapped = DefinitionImport.MapRow(new JsonObject { ["note"] = "  " }, fields);
        Assert.False(mapped.ContainsKey("note"));
    }

    // XDocument.Parse processes the DTD, a few hundred bytes of nested internal entities expand into gigabytes of string before anything else looks at the file.
    [Fact]
    public void An_xml_file_carrying_a_dtd_is_refused_rather_than_expanded()
    {
        var bomb = "<?xml version='1.0'?><!DOCTYPE l [<!ENTITY a 'aa'><!ENTITY b '&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;'>]><l><i>&b;</i></l>";
        var (rows, error) = Parse(bomb, "bomb.xml");
        Assert.Empty(rows);
        Assert.NotNull(error);
    }

    [Fact]
    public void An_xml_file_naming_a_local_file_as_an_entity_is_refused_too()
    {
        var xxe = "<?xml version='1.0'?><!DOCTYPE r [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><r><i>&x;</i></r>";
        Assert.NotNull(Parse(xxe, "xxe.xml").Error);
    }

    // The cap is refused while reading, not after: an 8 MB file of one-byte rows is a few hundred thousand objects if the whole file is materialised first.
    [Fact]
    public void A_file_over_the_row_cap_stops_being_read_at_the_cap()
    {
        var sb = new StringBuilder("a\n");
        for (var i = 0; i < DefinitionImport.MaxRows * 4; i++) sb.Append(i).Append('\n');
        var (rows, error) = Parse(sb.ToString(), "big.csv");
        Assert.Empty(rows);
        Assert.Contains("limit", error);
    }

    // The dedupe suffix used to share its counter with Position, two clashing headers renumbered the fields after them.
    [Fact]
    public void Headers_that_clash_do_not_disturb_the_order_of_the_fields_around_them()
    {
        var (rows, _) = Parse("one,a b,a-b,two\n1,2,3,4\n", "clash.csv");
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(rows));

        Assert.Equal(new[] { 0, 1, 2, 3 }, fields.Select(f => f.Position));
        Assert.Equal(new[] { "one", "a_b", "a_b_2", "two" }, fields.Select(f => f.Name));
    }

    // A nested object is imported as a free-form json field: inferring a sub-schema would invent field definitions for names nobody validated.
    [Fact]
    public void A_nested_object_becomes_a_free_form_json_field_rather_than_an_invented_sub_schema()
    {
        var (rows, error) = Parse("""[{"id":"1","meta":{"a b":1,"$$$":2}}]""", "nested.json");
        Assert.Null(error);
        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(rows));
        var meta = fields.First(f => f.Name == "meta");
        Assert.Equal("json", meta.DataType);
        Assert.Equal("[]", meta.OptionsJson);
    }

    // Two headers reaching one field would otherwise have whichever came last silently overwrite the other.
    [Fact]
    public void Only_one_header_may_feed_a_field()
    {
        var fields = new List<FieldDefinition> { new() { Name = "First_Name", Label = "First Name" } };
        var row = new JsonObject { ["First Name"] = "Ada", ["First_Name"] = "Grace" };
        var mapped = DefinitionImport.ColumnMap.For(new[] { row }, fields).Apply(row);
        Assert.Single(mapped);
        Assert.Equal("Ada", mapped["First_Name"]!.GetValue<string>());
    }

    // A wrapper holding one record each scores higher than the record itself: every one of the record's columns is also the wrapper's, only prefixed.
    [Fact]
    public void A_one_to_one_wrapper_loses_to_the_record_it_wraps()
    {
        var xml = """
        <templates>
          <category name="onboarding">
            <template id="welcome"><meta><name>Welcome</name></meta><subject>Hi</subject></template>
          </category>
          <category name="billing">
            <template id="invoice"><meta><name>Invoice</name></meta><subject>Due</subject></template>
          </category>
        </templates>
        """;
        var (rows, error) = Parse(xml, "t.xml");
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
        Assert.Equal("welcome", rows[0]["id"]!.GetValue<string>());
        Assert.Equal("Welcome", rows[0]["meta_name"]!.GetValue<string>());
    }

    // Grouping siblings per parent finds two groups of one and picks the wrapper; the records are one group of four spread over two parents.
    [Fact]
    public void Records_spread_over_several_parents_are_still_one_set_of_rows()
    {
        var xml = """
        <root>
          <group><item><sku>A</sku><qty>1</qty></item><item><sku>B</sku><qty>2</qty></item></group>
          <group><item><sku>C</sku><qty>3</qty></item><item><sku>D</sku><qty>4</qty></item></group>
        </root>
        """;
        var (rows, error) = Parse(xml, "t.xml");
        Assert.Null(error);
        Assert.Equal(4, rows.Count);
    }

    // Two siblings of one name are two values of one column; overwriting meant the first body of an email template was silently dropped.
    [Fact]
    public void Repeated_child_elements_become_a_list_rather_than_the_last_one_winning()
    {
        var xml = "<r><t id='1'><body>plain</body><body>html</body></t><t id='2'><body>x</body><body>y</body></t></r>";
        var (rows, error) = Parse(xml, "t.xml");
        Assert.Null(error);
        Assert.Equal(new[] { "plain", "html" }, rows[0]["body"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void Cdata_reads_as_text_and_a_namespace_prefix_is_not_part_of_the_column_name()
    {
        var xml = """
        <t:items xmlns:t="https://example.com/ns">
          <t:item id="1"><t:subject><![CDATA[Hello, {{name}}!]]></t:subject></t:item>
          <t:item id="2"><t:subject><![CDATA[Bye]]></t:subject></t:item>
        </t:items>
        """;
        var (rows, error) = Parse(xml, "t.xml");
        Assert.Null(error);
        Assert.Equal("Hello, {{name}}!", rows[0]["subject"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_and_self_closing_elements_contribute_no_columns()
    {
        var xml = "<form><input name='u' type='text'/><input name='p' type='password'/><br/><empty></empty><ws>   </ws></form>";
        var (rows, error) = Parse(xml, "t.xml");
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "name", "type" }, rows[0].Select(kv => kv.Key));
    }

    [Fact]
    public void Every_field_an_import_infers_passes_the_validator_that_now_gates_it()
    {
        // Table creation validates its inline fields, so an inferred schema the validator
        // refuses would turn a file that imported yesterday into a 400 with no way to fix it
        // short of editing the file.
        var csv = "Order No,First Name,e-mail,Total,When,Status,Site,Flag\n"
                + "A-1,Ann,a@b.com,12.5,2026-01-02,open,https://example.com,true\n"
                + "A-2,Bob,c@d.com,3,2026-01-03,closed,https://example.org,false\n"
                + "A-3,Cid,e@f.com,9,2026-01-04,open,https://example.net,true\n";
        var (rows, error) = Parse(csv, "orders.csv");
        Assert.Null(error);

        var fields = DefinitionImport.ToFields(DefinitionImport.InferFields(rows));
        Assert.NotEmpty(fields);

        var all = fields.Select(f => f.Name).ToList();
        for (var i = 0; i < fields.Count; i++)
        {
            var others = fields.Where((_, j) => j != i).Select(f => f.Name).ToList();
            Assert.Empty(FieldValidation.ValidateFieldDefinition(fields[i], others, all, _ => true));
        }
    }
}
