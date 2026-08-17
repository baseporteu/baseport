using Xunit;
using System.Text.Json.Nodes;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// field types from GAP-TRAILBASE.md §12, plus the nested-json validation bug
public class FieldValidationNewTypesTests
{
    private static FieldDefinition Field(string type, double? min = null, double? max = null, string optionsJson = "{}") =>
        new() { Id = Ids.NewShortId(12), Name = "F", DataType = type, Min = min, Max = max, OptionsJson = optionsJson };

    private static List<string> Validate(FieldDefinition f, JsonNode? value) =>
        FieldValidation.ValidateFieldValue(f, value, _ => true);

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    public void Email_validates_address_shape(string value, bool valid)
    {
        var errs = Validate(Field("email"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Theory]
    [InlineData("+31 6 1234 5678", true)]
    [InlineData("call me", false)]
    public void Phone_validates_loose_shape(string value, bool valid)
    {
        var errs = Validate(Field("phone"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    public void Url_requires_http_or_https(string value, bool valid)
    {
        var errs = Validate(Field("url"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Theory]
    [InlineData("#fff", true)]
    [InlineData("#a1b2c3", true)]
    [InlineData("rgb(10, 20, 30)", true)]
    [InlineData("hsl(120, 50%, 50%)", true)]
    [InlineData("chartreuse", false)]
    public void Color_accepts_hex_rgb_and_hsl(string value, bool valid)
    {
        var errs = Validate(Field("color"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Theory]
    [InlineData("14:30:00", true)]
    [InlineData("14:30", true)]
    [InlineData("not a time", false)]
    public void Time_parses_time_only(string value, bool valid)
    {
        var errs = Validate(Field("time"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Fact]
    public void Rating_behaves_like_a_bounded_number()
    {
        var f = Field("rating", min: 1, max: 5);
        Assert.Empty(Validate(f, JsonValue.Create(3.0)));
        Assert.Contains(Validate(f, JsonValue.Create(9.0)), e => e.Contains("at most 5"));
        Assert.Contains(Validate(f, JsonValue.Create("not a number")), e => e.Contains("must be a number"));
    }

    [Fact]
    public void Rating_definition_defaults_to_one_to_five_when_unset()
    {
        var f = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Stars", DataType = "rating" };
        var errs = FieldValidation.ValidateFieldDefinition(f, new List<string>(), new List<string> { "Stars" }, _ => true);
        Assert.Empty(errs);
        Assert.Equal(1, f.Min);
        Assert.Equal(5, f.Max);
    }

    [Theory]
    [InlineData("my-slug-123", true)]
    [InlineData("My Slug", false)]
    [InlineData("has_underscore", false)]
    public void Slug_requires_lowercase_hyphenated_shape(string value, bool valid)
    {
        var errs = Validate(Field("slug"), JsonValue.Create(value));
        Assert.Equal(valid, errs.Count == 0);
    }

    [Fact]
    public void Slug_definition_rejects_a_source_field_that_does_not_exist()
    {
        var f = new FieldDefinition { Id = Ids.NewShortId(12), Name = "Slug", DataType = "slug", OptionsJson = """{"sourceField":"Ghost"}""" };
        var errs = FieldValidation.ValidateFieldDefinition(f, new List<string> { "Title" }, new List<string> { "Title", "Slug" }, _ => true);
        Assert.Contains(errs, e => e.Contains("Ghost"));
    }

    [Fact]
    public void Json_field_requires_an_object_not_an_array_or_scalar()
    {
        var f = Field("json");
        Assert.Empty(Validate(f, JsonNode.Parse("""{"a":1}""")));
        Assert.Contains(Validate(f, JsonNode.Parse("[1,2]")), e => e.Contains("JSON object"));
        Assert.Contains(Validate(f, JsonValue.Create("plain string")), e => e.Contains("JSON object"));
    }

    [Fact]
    public void Array_field_accepts_only_scalar_items()
    {
        var f = Field("array");
        Assert.Empty(Validate(f, JsonNode.Parse("""["a", 1, true]""")));
        Assert.Contains(Validate(f, JsonNode.Parse("""[{"nested":true}]""")), e => e.Contains("nested objects"));
        Assert.Contains(Validate(f, JsonNode.Parse("""{"not":"an array"}""")), e => e.Contains("list of values"));
    }

    [Fact]
    public void Array_field_enforces_an_item_cap()
    {
        var f = Field("array", max: 2);
        Assert.Contains(Validate(f, JsonNode.Parse("[1,2,3]")), e => e.Contains("at most 2"));
    }

    [Fact]
    public void Array_field_with_columns_validates_line_item_rows()
    {
        var f = Field("array", optionsJson: """{"columns":[{"name":"Qty","dataType":"number"},{"name":"Price","dataType":"currency"}]}""");

        Assert.Empty(Validate(f, JsonNode.Parse("""[{"Qty":2,"Price":9.5},{"Qty":1,"Price":3}]""")));
        Assert.Contains(Validate(f, JsonNode.Parse("""[{"Qty":"not a number","Price":1}]""")), e => e.Contains("Qty must be a number"));
        Assert.Contains(Validate(f, JsonNode.Parse("""[{"Qty":1,"Sku":"ABC"}]""")), e => e.Contains("unknown column 'Sku'"));
        Assert.Contains(Validate(f, JsonNode.Parse("""["not an object"]""")), e => e.Contains("must contain line-item rows"));
    }

    [Fact]
    public void Password_field_validates_plaintext_length_but_not_a_stored_hash()
    {
        var f = Field("password", min: 10);
        Assert.Contains(Validate(f, JsonValue.Create("short")), e => e.Contains("at least 10"));
        Assert.Empty(Validate(f, JsonValue.Create("a very long passphrase")));
        // already-hashed values skip content validation entirely
        Assert.Empty(Validate(f, JsonValue.Create("pbkdf2$210000$c2FsdA==$aGFzaA==")));
    }

    // regression: a text field with no Min used to silently accept nested json instead of rejecting it
    [Theory]
    [InlineData("text")]
    [InlineData("longtext")]
    [InlineData("richtext")]
    [InlineData("email")]
    [InlineData("number")]
    [InlineData("select")]
    public void Nested_json_is_rejected_on_every_scalar_type_not_silently_swallowed(string type)
    {
        var f = Field(type, optionsJson: """["red","blue"]""");
        var errs = Validate(f, JsonNode.Parse("""{"injected":"object"}"""));
        Assert.Contains(errs, e => e.Contains("nested object or array"));
    }

    [Fact]
    public void Multiselect_and_json_and_array_are_exempt_from_the_scalar_guard()
    {
        // only these three ever have a JsonArray/JsonObject value, not JsonValue
        Assert.Empty(Validate(Field("multiselect", optionsJson: """["a","b"]"""), JsonNode.Parse("""["a"]""")));
        Assert.Empty(Validate(Field("json"), JsonNode.Parse("""{"k":"v"}""")));
        Assert.Empty(Validate(Field("array"), JsonNode.Parse("""["a",1]""")));
    }
}

// write-path mutations: slug derivation, richtext sanitization, password hashing and redaction
public class RecordEngineNewTypesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordEngineNewTypesTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TableDefinition Seed(params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Posts", Fields = fields.ToList() };
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    private static JsonObject Json(string raw) => (JsonObject)JsonNode.Parse(raw)!;

    [Fact]
    public async Task Slug_is_derived_from_its_source_field_when_left_blank()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Title", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Slug", DataType = "slug", OptionsJson = """{"sourceField":"Title"}""", IsRequired = true });

        var obj = Json("""{ "Title": "Hello, World! It's a Test" }""");
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(outcome.Errors);
        Assert.Equal("hello-world-it-s-a-test", obj["Slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Slug_supplied_by_the_caller_is_never_overwritten()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Title", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Slug", DataType = "slug", OptionsJson = """{"sourceField":"Title"}""" });

        var obj = Json("""{ "Title": "Hello World", "Slug": "custom-slug" }""");
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(outcome.Errors);
        Assert.Equal("custom-slug", obj["Slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Richtext_is_sanitized_before_storage()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Body", DataType = "richtext" });
        var obj = Json("""{ "Body": "<p>hi</p><script>alert(1)</script>" }""");

        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(outcome.Errors);
        var stored = obj["Body"]!.GetValue<string>();
        Assert.Contains("<p>hi</p>", stored);
        Assert.DoesNotContain("<script>", stored);
    }

    [Fact]
    public async Task Password_is_hashed_on_write_and_the_hash_is_never_rehashed_on_resave()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Password", DataType = "password" });
        var obj = Json("""{ "Password": "correct horse battery staple" }""");

        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);
        Assert.Empty(outcome.Errors);
        var hashed = obj["Password"]!.GetValue<string>();
        Assert.StartsWith("pbkdf2$", hashed);
        Assert.True(AdminAuth.VerifyPassword("correct horse battery staple", hashed));

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        _db.SaveChanges();

        // a resave that doesn't touch the password must not re-hash the stored hash
        var (merged, resaveOutcome) = await RecordEngine.ApplyUpdateAsync(_db, table, table.Fields, record, Json("{}"), replace: false);
        Assert.Empty(resaveOutcome.Errors);
        Assert.Equal(hashed, merged["Password"]!.GetValue<string>());
    }

    [Fact]
    public async Task Password_is_omitted_from_every_read_response()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Username", DataType = "text" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Password", DataType = "password" });

        var obj = Json("""{ "Username": "alice", "Password": "hunter2hunter2" }""");
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);
        Assert.Empty(outcome.Errors);

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        var dto = ApiDtos.RecordDto(record, table.Fields);
        var json = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("hunter2hunter2", json);
        Assert.DoesNotContain("pbkdf2$", json);
        Assert.Contains("alice", json);
    }

    [Fact]
    public async Task Json_and_array_fields_round_trip_through_the_write_path()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Meta", DataType = "json" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Tags", DataType = "array" });

        var obj = Json("""{ "Meta": { "source": "import" }, "Tags": ["a", "b", 3] }""");
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);

        Assert.Empty(outcome.Errors);
        Assert.Equal("import", obj["Meta"]!["source"]!.GetValue<string>());
        Assert.Equal(3, ((JsonArray)obj["Tags"]!).Count);
    }
}
