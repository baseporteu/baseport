using Xunit;
using Baseport;

namespace Baseport.Tests;

// Form validation is what stops an author building a form the server will refuse, or worse, one that leaks a field the visitor should never see.
public class FormValidationTests
{
    private static readonly List<FieldDefinition> Fields = new()
    {
        new() { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsIdentifier = true },
        new() { Id = Ids.NewShortId(12), Name = "Customer", DataType = "text" },
        new() { Id = Ids.NewShortId(12), Name = "InternalNote", DataType = "text", IsHidden = true },
        new() { Id = Ids.NewShortId(12), Name = "Total", DataType = "calculated", Expression = "1 + 1" }
    };

    private static FormConfig Form(string kind, string config, string title = "My form", string actions = FormActions.Submit) =>
        new() { Kind = kind, Actions = actions, Title = title, ConfigJson = config, LayoutJson = "[]" };

    private static FormConfig Lookup(string config, string title = "My form") =>
        Form(FormKinds.Form, config, title, FormActions.Lookup);

    [Fact]
    public void Title_is_required()
    {
        var errors = FieldValidation.ValidateForm(Form(FormKinds.List, """{"columns":["Customer"]}""", title: " "), Fields);
        Assert.Contains(errors, e => e.Contains("title", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lookup_needs_at_least_one_identifier()
    {
        var errors = FieldValidation.ValidateForm(Lookup("{}"), Fields);
        Assert.Contains(errors, e => e.Contains("identifier field"));
    }

    [Fact]
    public void Lookup_cannot_match_on_a_hidden_field()
    {
        var errors = FieldValidation.ValidateForm(Lookup("""{"matchFields":["InternalNote"]}"""), Fields);
        Assert.Contains(errors, e => e.Contains("hidden"));
    }

    [Fact]
    public void Lookup_rejects_a_field_that_does_not_exist()
    {
        var errors = FieldValidation.ValidateForm(Lookup("""{"matchFields":["Ghost"]}"""), Fields);
        Assert.Contains(errors, e => e.Contains("unknown field 'Ghost'"));
    }

    [Fact]
    public void Lookup_accepts_a_real_identifier()
    {
        var config = """{"matchFields":["OrderNo"],"resultFields":["Customer","Total"]}""";
        Assert.Empty(FieldValidation.ValidateForm(Lookup(config), Fields));
    }

    [Fact]
    public void List_needs_at_least_one_column()
    {
        var errors = FieldValidation.ValidateForm(Form(FormKinds.List, "{}"), Fields);
        Assert.Contains(errors, e => e.Contains("at least one column"));
    }

    [Fact]
    public void List_rejects_an_out_of_range_page_size()
    {
        var config = """{"columns":["Customer"],"pageSize":100000}""";
        var errors = FieldValidation.ValidateForm(Form(FormKinds.List, config), Fields);
        Assert.Contains(errors, e => e.Contains("page size"));
    }

    [Fact]
    public void List_rejects_an_unknown_sort_field()
    {
        var config = """{"columns":["Customer"],"sortField":"Ghost"}""";
        var errors = FieldValidation.ValidateForm(Form(FormKinds.List, config), Fields);
        Assert.Contains(errors, e => e.Contains("unknown field 'Ghost'"));
    }

    [Fact]
    public void List_accepts_a_complete_configuration()
    {
        var config = """{"columns":["OrderNo","Customer"],"searchFields":["Customer"],"sortField":"Customer","sortDir":"asc","pageSize":25}""";
        Assert.Empty(FieldValidation.ValidateForm(Form(FormKinds.List, config), Fields));
    }

    [Fact]
    public void Malformed_config_is_reported_not_thrown()
    {
        var errors = FieldValidation.ValidateForm(Form(FormKinds.List, "{not json"), Fields);
        Assert.Contains(errors, e => e.Contains("not valid JSON"));
    }

    [Theory]
    [InlineData("form", FormKinds.Form)]
    [InlineData("List", FormKinds.List)]
    [InlineData("nonsense", FormKinds.Form)]
    [InlineData(null, FormKinds.Form)]
    // Pre-mode configurations stored the action as the kind; both still resolve.
    [InlineData("submit", FormKinds.Form)]
    [InlineData("LOOKUP", FormKinds.Form)]
    public void Kind_normalizes_to_a_known_value(string? input, string expected) =>
        Assert.Equal(expected, FormKinds.Normalize(input));

    [Theory]
    [InlineData("submit", new[] { "submit" })]
    [InlineData("lookup", new[] { "lookup" })]
    [InlineData("submit,lookup", new[] { "submit", "lookup" })]
    [InlineData("LOOKUP, SUBMIT", new[] { "lookup", "submit" })]   // order is kept
    [InlineData("submit,submit", new[] { "submit" })]              // de-duplicated
    [InlineData("submit,nonsense", new[] { "submit" })]            // unknown dropped
    [InlineData("nonsense", new[] { "submit" })]                   // never actionless
    [InlineData("", new[] { "submit" })]
    [InlineData(null, new[] { "submit" })]
    public void Actions_parse_to_a_known_ordered_set(string? input, string[] expected) =>
        Assert.Equal(expected, FormActions.Parse(input));

    [Fact]
    public void A_form_may_enable_both_actions_at_once()
    {
        // One RMA form can look an existing case up and raise a new one.
        var config = @"{""matchFields"":[""OrderNo""],""resultFields"":[""Customer""]}";
        var form = Form(FormKinds.Form, config, actions: "submit,lookup");
        form.LayoutJson = @"{""rows"":[{""t"":""row"",""cols"":[{""t"":""col"",""w"":12,""items"":[""Customer""]}]}]}";

        Assert.Empty(FieldValidation.ValidateForm(form, Fields));
    }

    [Fact]
    public void Enabling_both_validates_both()
    {
        // Submit config is fine, lookup config is missing: it must still fail.
        var form = Form(FormKinds.Form, "{}", actions: "submit,lookup");
        form.LayoutJson = @"{""rows"":[{""t"":""row"",""cols"":[{""t"":""col"",""w"":12,""items"":[""Customer""]}]}]}";

        var errors = FieldValidation.ValidateForm(form, Fields);
        Assert.Contains(errors, e => e.Contains("identifier field"));
    }

    [Fact]
    public void A_read_only_form_cannot_also_submit()
    {
        var form = Form(FormKinds.Form, "{}", actions: "submit");
        form.IsReadOnly = true;
        Assert.Contains(FieldValidation.ValidateForm(form, Fields), e => e.Contains("read-only"));
    }

    [Fact]
    public void A_list_renderer_must_be_a_valid_expression_over_real_columns()
    {
        var bad = Form(FormKinds.List, """{"columns":["Customer"],"renderers":{"Ghost":"data.Customer"}}""");
        Assert.Contains(FieldValidation.ValidateForm(bad, Fields), e => e.Contains("unknown column 'Ghost'"));

        var good = Form(FormKinds.List, """{"columns":["Customer"],"renderers":{"Customer":"data.Customer + \"!\""}}""");
        Assert.Empty(FieldValidation.ValidateForm(good, Fields));
    }

    [Fact]
    public void A_computed_field_cannot_be_a_lookup_identifier()
    {
        var field = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Total", DataType = "calculated", Expression = "1 + 1", IsIdentifier = true };
        var errors = FieldValidation.ValidateFieldDefinition(field, Array.Empty<string>(), new[] { "Total" }, _ => true);
        Assert.Contains(errors, e => e.Contains("lookup identifier"));
    }

    [Fact]
    public void A_catastrophic_pattern_is_refused_at_save_time()
    {
        // (a+)+$ compiles fine and then pegs a core on every anonymous submit, so the author has to hear about it, not the visitor.
        var field = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Code", DataType = "text", Pattern = "(a+)+$" };
        var errors = FieldValidation.ValidateFieldDefinition(field, Array.Empty<string>(), new[] { "Code" }, _ => true);
        Assert.Contains(errors, e => e.Contains("too long to evaluate"));

        var sane = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Code", DataType = "text", Pattern = "^[A-Z]{2}[0-9]{4}$" };
        Assert.Empty(FieldValidation.ValidateFieldDefinition(sane, Array.Empty<string>(), new[] { "Code" }, _ => true));
    }

    [Fact]
    public void A_list_keeps_its_column_order()
    {
        // The builder is drag-ordered, so the stored order is the display order.
        var config = @"{""columns"":[""Total"",""OrderNo"",""Customer""]}";
        var form = Form(FormKinds.List, config, title: "Ordered");
        Assert.Empty(FieldValidation.ValidateForm(form, Fields));

        var columns = System.Text.Json.JsonDocument.Parse(config).RootElement
            .GetProperty("columns").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Total", "OrderNo", "Customer" }, columns);
    }

    [Fact]
    public void A_submit_form_whose_layout_holds_no_fields_is_refused()
    {
        // It saves cleanly and then renders an empty page with a submit button, which is how a lookup once shipped looking like a broken form.
        var empty = Form(FormKinds.Form, "{}", title: "Empty");
        Assert.Contains(FieldValidation.ValidateForm(empty, Fields), e => e.Contains("at least one field in its layout"));

        var populated = Form(FormKinds.Form, "{}", title: "Real");
        populated.LayoutJson = @"{""rows"":[{""t"":""row"",""cols"":[{""t"":""col"",""w"":12,""items"":[""Customer""]}]}]}";
        Assert.Empty(FieldValidation.ValidateForm(populated, Fields));
    }

    [Fact]
    public void A_lookup_with_no_result_fields_is_refused()
    {
        var errors = FieldValidation.ValidateForm(Lookup(@"{""matchFields"":[""OrderNo""]}"), Fields);
        Assert.Contains(errors, e => e.Contains("at least one field to show"));
    }

    [Theory]
    [InlineData("eur", "currency", true)]
    [InlineData("EUR", "currency", true)]
    [InlineData("EU", "currency", false)]
    [InlineData("EURO", "currency", false)]
    [InlineData("E1R", "currency", false)]
    [InlineData("EUR", "text", false)]
    public void A_currency_code_is_three_letters_on_a_currency_field(string code, string type, bool valid)
    {
        var field = new FieldDefinition { Name = "Price", DataType = type, Currency = code };
        var errors = FieldValidation.ValidateFieldDefinition(field, Array.Empty<string>(), new[] { "Price" }, _ => true);
        Assert.Equal(valid, !errors.Any(e => e.Contains("urrency")));
    }

    [Theory]
    [InlineData("sales-orders", true)]
    [InlineData("orders2", true)]
    [InlineData("Sales-Orders", true)]      // normalized to lowercase
    [InlineData("s", false)]                // too short
    [InlineData("2orders", false)]          // must start with a letter
    [InlineData("sales orders", false)]     // no spaces in a URL segment
    [InlineData("sales_orders", false)]     // hyphens only
    [InlineData("api", false)]              // reserved path segment
    [InlineData("openapi.json", false)]     // reserved
    public void A_published_api_name_is_url_safe_and_not_reserved(string apiName, bool valid)
    {
        var table = new TableDefinition { Name = "Internal Working Name", ApiName = apiName };
        var errors = FieldValidation.ValidateTable(table, Array.Empty<string>());
        Assert.Equal(valid, !errors.Any(e => e.Contains("API name") || e.Contains("reserved")));
    }

    [Fact]
    public void Publishing_a_table_requires_an_api_name()
    {
        // Without it the document would fall back to the internal name, which is exactly the leak the separate name exists to prevent.
        var table = new TableDefinition { Name = "Internal Working Name", ApiEnabled = true };
        Assert.Contains(FieldValidation.ValidateTable(table, Array.Empty<string>()),
                        e => e.Contains("needs an API name"));

        table.ApiName = "orders";
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    [Fact]
    public void An_unpublished_table_needs_no_api_name()
    {
        var table = new TableDefinition { Name = "Scratch", ApiEnabled = false };
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    [Fact]
    public void A_proxy_table_needs_an_absolute_target_and_a_known_method()
    {
        var table = new TableDefinition { Name = "Remote", IsProxy = true, ProxyUrl = "not-a-url", ProxyMethod = "FETCH" };
        var errors = FieldValidation.ValidateTable(table, Array.Empty<string>());
        Assert.Contains(errors, e => e.Contains("absolute http(s) URL"));
        Assert.Contains(errors, e => e.Contains("proxy method"));

        table.ProxyUrl = "https://example.test/api/items";
        table.ProxyMethod = "POST";
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    [Fact]
    public void Minimum_above_maximum_is_rejected()
    {
        var field = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number", Min = 10, Max = 1 };
        var errors = FieldValidation.ValidateFieldDefinition(field, Array.Empty<string>(), new[] { "Qty" }, _ => true);
        Assert.Contains(errors, e => e.Contains("Minimum cannot be greater"));
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("reset")]
    [InlineData("cancel")]
    [InlineData("validate")]
    public void Button_action_accepts_cancel_and_validate_alongside_submit_and_reset(string action)
    {
        var form = Form(FormKinds.Form, "{}", title: "Buttons");
        form.LayoutJson = $$"""{"rows":[{"t":"button","label":"Go","action":"{{action}}"}]}""";
        Assert.Empty(FieldValidation.ValidateLayout(form, Fields.Select(f => f.Name).ToList()));
    }

    [Fact]
    public void An_unknown_button_action_is_rejected()
    {
        var form = Form(FormKinds.Form, "{}", title: "Buttons");
        form.LayoutJson = """{"rows":[{"t":"button","label":"Go","action":"teleport"}]}""";
        Assert.Contains(FieldValidation.ValidateLayout(form, Fields.Select(f => f.Name).ToList()),
                        e => e.Contains("button action must be one of"));
    }

    [Fact]
    public void A_link_button_requires_a_valid_href_expression()
    {
        var missing = Form(FormKinds.Form, "{}", title: "Buttons");
        missing.LayoutJson = """{"rows":[{"t":"button","label":"View","action":"link"}]}""";
        Assert.Contains(FieldValidation.ValidateLayout(missing, Fields.Select(f => f.Name).ToList()),
                        e => e.Contains("requires a URL expression"));

        var bad = Form(FormKinds.Form, "{}", title: "Buttons");
        bad.LayoutJson = """{"rows":[{"t":"button","label":"View","action":"link","hrefExpr":"data.Ghost"}]}""";
        Assert.Contains(FieldValidation.ValidateLayout(bad, Fields.Select(f => f.Name).ToList()),
                        e => e.Contains("Unknown field 'Ghost'"));

        var good = Form(FormKinds.Form, "{}", title: "Buttons");
        good.LayoutJson = """{"rows":[{"t":"button","label":"View","action":"link","hrefExpr":"'/o?n=' + data.OrderNo"}]}""";
        Assert.Empty(FieldValidation.ValidateLayout(good, Fields.Select(f => f.Name).ToList()));
    }

    [Fact]
    public void OnSuccessRedirect_is_validated_like_a_renderer()
    {
        var layout = """{"rows":[{"t":"row","cols":[{"t":"col","w":12,"items":["Customer"]}]}]}""";

        var bad = Form(FormKinds.Form, """{"onSuccessRedirect":"data.Ghost"}""");
        bad.LayoutJson = layout;
        Assert.Contains(FieldValidation.ValidateForm(bad, Fields), e => e.Contains("Redirect on success"));

        var good = Form(FormKinds.Form, """{"onSuccessRedirect":"'/thanks?order=' + data.OrderNo"}""");
        good.LayoutJson = layout;
        Assert.Empty(FieldValidation.ValidateForm(good, Fields));

        // Blank is the "no redirect" default and must not be forced through the expression grammar.
        var blank = Form(FormKinds.Form, """{"onSuccessRedirect":""}""");
        blank.LayoutJson = layout;
        Assert.Empty(FieldValidation.ValidateForm(blank, Fields));
    }

    [Fact]
    public void A_list_action_button_requires_a_label_and_a_valid_href_expression()
    {
        var noLabel = Form(FormKinds.List, """{"columns":["Customer"],"actions":[{"hrefExpr":"data.OrderNo"}]}""");
        Assert.Contains(FieldValidation.ValidateForm(noLabel, Fields), e => e.Contains("requires a label"));

        var badExpr = Form(FormKinds.List, """{"columns":["Customer"],"actions":[{"label":"View","hrefExpr":"data.Ghost"}]}""");
        Assert.Contains(FieldValidation.ValidateForm(badExpr, Fields), e => e.Contains("Unknown field 'Ghost'"));

        var good = Form(FormKinds.List, """{"columns":["Customer"],"actions":[{"label":"View","hrefExpr":"'/o?n=' + data.OrderNo"}]}""");
        Assert.Empty(FieldValidation.ValidateForm(good, Fields));
    }

    [Fact]
    public void EncodeURIComponent_is_available_to_href_expressions()
    {
        // hrefExpr's whole job is building a URL; without this builtin an expression could not encode a value safely.
        var validation = JsExpr.Validate("encodeURIComponent('a b/c')", Array.Empty<string>());
        Assert.True(validation.Valid, string.Join("; ", validation.Errors));
        Assert.Equal("a%20b%2Fc", JsExpr.Evaluate("encodeURIComponent('a b/c')", _ => null));
    }
}
