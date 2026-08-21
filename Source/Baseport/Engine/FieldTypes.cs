using System.Collections.Frozen;

namespace Baseport;

// Validation, the OpenAPI schema and the wire catalog branch on this instead of on a list of type names.
public enum FieldShape { Scalar, Object, Array }

public readonly record struct TdsColumnType(string Name, int SystemTypeId, int MaxLength, int Precision, int Scale)
{
    public static readonly TdsColumnType NVarChar = new("nvarchar", 231, 8000, 0, 0);
    public static readonly TdsColumnType NVarCharMax = new("nvarchar", 231, -1, 0, 0);
    public static readonly TdsColumnType Bit = new("bit", 104, 1, 1, 0);
    public static readonly TdsColumnType Decimal = new("decimal", 106, 17, 18, 6);
    public static readonly TdsColumnType Date = new("date", 40, 3, 10, 0);
    public static readonly TdsColumnType Time = new("time", 41, 5, 16, 7);
    public static readonly TdsColumnType DateTime2 = new("datetime2", 42, 8, 27, 7);
}

// What the picker groups a type under. Ordered the way the list is drawn, most-reached-for first.
public static class FieldGroups
{
    public const string Text = "Text";
    public const string Numbers = "Numbers";
    public const string Time = "Date and time";
    public const string Choice = "Choice";
    public const string Structured = "Structured";
    public const string Computed = "Server owned";

    public static readonly IReadOnlyList<string> Order = [Text, Numbers, Time, Choice, Structured, Computed];
}

// One row per field type. FieldDefinition.DataType is a key into this table, and nothing else interprets it.
public sealed record FieldType(string Name, string Label, string Group, FieldShape Shape = FieldShape.Scalar)
{
    public string JsonType { get; init; } = "string";
    public string? JsonFormat { get; init; }
    public string PostgresType { get; init; } = "text";
    public int PostgresOid { get; init; } = 25;
    public TdsColumnType Tds { get; init; } = TdsColumnType.NVarChar;

    // Worth a generated column and an index on _records.
    public bool Indexable { get; init; }

    // Filled in by the server, so it is never validated, never required and never a lookup identifier.
    public bool Computed { get; init; }

    // Stripped from every read path.
    public bool Secret { get; init; }

    // A type whose write path only runs over a top-level field cannot be a member of a nested object.
    public bool Nestable { get; init; } = true;

    // Accepted on input and normalized to Name.
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public static class FieldTypes
{
    private static readonly FieldType Number = new("number", "Number", FieldGroups.Numbers) { JsonType = "number", PostgresType = "numeric", PostgresOid = 1700, Tds = TdsColumnType.Decimal, Indexable = true };

    public static readonly IReadOnlyList<FieldType> All =
    [
        new("text", "Text", FieldGroups.Text) { Indexable = true },
        new("longtext", "Long text", FieldGroups.Text) { Tds = TdsColumnType.NVarCharMax, Aliases = ["markdown"] },
        new("richtext", "Rich text", FieldGroups.Text) { Tds = TdsColumnType.NVarCharMax, Aliases = ["html"] },
        new("email", "Email", FieldGroups.Text) { JsonFormat = "email", Indexable = true },
        new("url", "URL", FieldGroups.Text) { JsonFormat = "uri", Indexable = true, Aliases = ["link"] },
        new("slug", "Slug", FieldGroups.Text) { Indexable = true, Nestable = false },
        new("password", "Password", FieldGroups.Text) { JsonFormat = "password", Secret = true, Nestable = false, Aliases = ["encrypted"] },

        Number,
        Number with { Name = "currency", Label = "Currency", Aliases = ["price"] },

        new("date", "Date", FieldGroups.Time) { JsonFormat = "date", PostgresType = "date", PostgresOid = 1082, Tds = TdsColumnType.Date, Indexable = true },
        new("datetime", "Date and time", FieldGroups.Time) { JsonFormat = "date-time", PostgresType = "timestamp without time zone", PostgresOid = 1114, Tds = TdsColumnType.DateTime2, Indexable = true, Aliases = ["timestamp"] },
        new("time", "Time of day", FieldGroups.Time) { JsonFormat = "time", PostgresType = "time", PostgresOid = 1083, Tds = TdsColumnType.Time, Indexable = true },

        new("boolean", "Yes or no", FieldGroups.Choice) { JsonType = "boolean", PostgresType = "boolean", PostgresOid = 16, Tds = TdsColumnType.Bit, Indexable = true, Aliases = ["checkbox", "bool"] },
        new("select", "Select one", FieldGroups.Choice) { Indexable = true },
        new("multiselect", "Select many", FieldGroups.Choice, FieldShape.Array) { JsonType = "array", PostgresType = "json", PostgresOid = 114, Tds = TdsColumnType.NVarCharMax, Aliases = ["tags"] },

        new("json", "Object", FieldGroups.Structured, FieldShape.Object) { JsonType = "object", PostgresType = "json", PostgresOid = 114, Tds = TdsColumnType.NVarCharMax, Aliases = ["object"] },
        new("array", "List", FieldGroups.Structured, FieldShape.Array) { JsonType = "array", PostgresType = "json", PostgresOid = 114, Tds = TdsColumnType.NVarCharMax, Aliases = ["list"] },
        new("file", "File", FieldGroups.Structured) { JsonFormat = "uri", Aliases = ["media"] },
        new("reference", "Reference", FieldGroups.Structured) { Indexable = true, Aliases = ["relation"] },

        new("calculated", "Calculated", FieldGroups.Computed) { Computed = true, Nestable = false, Aliases = ["formula"] },
        new("derived", "Derived", FieldGroups.Computed) { Computed = true, Nestable = false, Aliases = ["internal"] },
        new("systemid", "System ID", FieldGroups.Computed) { Computed = true, Indexable = true, Nestable = false, Aliases = ["system_id"] },
    ];

    private static readonly FrozenDictionary<string, FieldType> ByName =
        All.SelectMany(t => t.Aliases.Append(t.Name), (t, key) => (key, t))
           .ToFrozenDictionary(x => x.key, x => x.t, StringComparer.OrdinalIgnoreCase);

    public static readonly FieldType Text = ByName["text"];

    // Null for an unknown type: the one caller that must reject rather than guess is field definition validation.
    public static FieldType? Find(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out var t) ? t : null;

    // Every read path: an unknown type on a stored field is treated as text rather than crashing a listing.
    public static FieldType Of(FieldDefinition field) => Find(field.DataType) ?? Text;
}
