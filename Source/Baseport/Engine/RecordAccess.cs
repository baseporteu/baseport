using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public enum Permission { Create, Read, Update, Delete }

// Per-record access rules, in the shape TrailBase uses: an author writes a SQLite boolean expression over _USER_, _ROW_ and _REQ_, and SQLite evaluates it. There is no expression language here, and deliberately so.
//
// TrailBase exposes _ROW_ by aliasing the table (crates/core/templates/list_record_query.sql). Baseport keeps records as JSON in one shared table, so a real alias would have to be a lateral join, which SQLite has no support for. The references are rewritten into json_extract against the same row instead, which leaves the author writing exactly the same rule.
public static partial class RecordAccess
{
    public static string RuleFor(TableDefinition table, Permission permission) => permission switch
    {
        Permission.Create => table.CreateRule,
        Permission.Read => table.ReadRule,
        Permission.Update => table.UpdateRule,
        Permission.Delete => table.DeleteRule,
        _ => ""
    };

    public static bool HasRule(TableDefinition table, Permission permission) =>
        !string.IsNullOrWhiteSpace(RuleFor(table, permission));

    public static readonly (string Key, Permission Permission)[] RuleKeys =
    [
        ("createRule", Permission.Create),
        ("readRule", Permission.Read),
        ("updateRule", Permission.Update),
        ("deleteRule", Permission.Delete)
    ];

    public static void Assign(TableDefinition table, Permission permission, string rule)
    {
        switch (permission)
        {
            case Permission.Create: table.CreateRule = rule; break;
            case Permission.Read: table.ReadRule = rule; break;
            case Permission.Update: table.UpdateRule = rule; break;
            case Permission.Delete: table.DeleteRule = rule; break;
        }
    }

    // The only check that catches a rule SQLite itself will not accept, so the author hears about it while saving instead of every caller hearing about it as a 500.
    public static async Task<string?> SqlProblemAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;

        var args = new List<object?>();
        var expression = Rewrite(rule, fields, "r", null, null, null, args);
        var sql = $"""
            SELECT COALESCE(CAST(({expression}) AS INTEGER), 0) AS "Value"
            FROM "_records" r WHERE r."TableId" = {Slot(args, table.Id)} LIMIT 1
            """;
        try
        {
            await db.Database.SqlQueryRaw<int>(sql, args.Select(a => a ?? DBNull.Value).ToArray()).ToListAsync();
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return $"SQLite rejected that rule: {ex.Message}";
        }
    }

    // A rule that names a field the table does not have would otherwise fail at request time, as a 500, on every call.
    public static string? Problem(string? rule, IReadOnlyList<FieldDefinition> fields)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        if (rule.Length > 2000) return "An access rule must be 2000 characters or fewer.";
        if (rule.Contains(';')) return "An access rule is a single expression and cannot contain ';'.";

        foreach (Match match in AliasReference().Matches(rule))
        {
            var alias = match.Groups["alias"].Value;
            var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;

            if (alias == "_USER_")
            {
                if (name != "id") return $"_USER_ has no '{name}'. Only _USER_.id is available.";
                continue;
            }
            if (fields.All(f => f.Name != name))
                return $"{alias}.{name} does not name a field on this table.";
        }

        foreach (Match match in UnknownAlias().Matches(rule))
            if (match.Value is not ("_USER_" or "_ROW_" or "_REQ_"))
                return $"{match.Value} is not one of _USER_, _ROW_ or _REQ_.";

        return null;
    }

    // Rewrites the author's rule into SQL over the shared record table, collecting the values every reference needs bound.
    // `rowAlias` names a SQL alias to read _ROW_ from; when it is null, _ROW_ is bound from `row`, or resolves to NULL when there is no row at all (create).
    internal static string Rewrite(string rule, IReadOnlyList<FieldDefinition> fields, string? rowAlias, string? userId, JsonObject? request, JsonObject? row, List<object?> args)
    {
        return AliasReference().Replace(rule, match =>
        {
            var alias = match.Groups["alias"].Value;
            var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;

            if (alias == "_USER_") return Slot(args, userId);
            if (alias == "_REQ_") return Slot(args, Value(request, name));

            var field = fields.FirstOrDefault(f => f.Name == name);
            if (field is null) return "NULL";
            if (rowAlias is not null) return QueryEngine.JsonPathFor(field.Name, rowAlias);
            return row is null ? "NULL" : Slot(args, Value(row, name));
        });
    }

    public static async Task<bool> AllowsAsync(
        AppDbContext db,
        TableDefinition table,
        IReadOnlyList<FieldDefinition> fields,
        Permission permission,
        string? userId,
        string? recordId = null,
        JsonObject? request = null,
        JsonObject? row = null)
    {
        var rule = RuleFor(table, permission);
        if (string.IsNullOrWhiteSpace(rule)) return true;

        var args = new List<object?>();
        var fromRow = recordId is not null;
        var expression = Rewrite(rule, fields, fromRow ? "r" : null, userId, request, row, args);

        string sql;
        if (fromRow)
        {
            var idSlot = Slot(args, recordId);
            var tableSlot = Slot(args, table.Id);
            sql = $"""
                SELECT COALESCE(CAST(({expression}) AS INTEGER), 0) AS "Value"
                FROM "_records" r WHERE r."Id" = {idSlot} AND r."TableId" = {tableSlot}
                """;
        }
        else
        {
            sql = $"""SELECT COALESCE(CAST(({expression}) AS INTEGER), 0) AS "Value" """;
        }

        // A missing row yields no result at all, which is a refusal rather than an error: the same answer TrailBase gives.
        var results = await db.Database.SqlQueryRaw<int>(sql, args.Select(a => a ?? DBNull.Value).ToArray()).ToListAsync();
        return results.Count > 0 && results[0] != 0;
    }

    // The read rule filters a listing rather than refusing it, so a caller sees the rows they may see instead of a 403. TrailBase makes the same choice (records/list_records.rs:251).
    public static string? ListClause(TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rowAlias, string? userId, List<object> args)
    {
        if (!HasRule(table, Permission.Read)) return null;

        var collected = new List<object?>();
        var expression = Rewrite(table.ReadRule, fields, rowAlias, userId, null, null, collected);

        // The list query numbers its own placeholders, so the slots are renumbered onto the end of its argument list.
        var offset = args.Count;
        args.AddRange(collected.Select(a => a ?? (object)DBNull.Value));
        return SlotToken().Replace(expression, m => $"{{{int.Parse(m.Groups["n"].Value) + offset}}}");
    }

    // The read rule as a self-contained boolean with its values inlined as SQL literals, for a context that cannot bind parameters: the wire providers build one static temp view per table. A read rule carries only _USER_.id, a system-issued short id, and it is escaped as a literal regardless. Returns null when the table has no read rule, so the caller leaves the view unfiltered.
    public static string? ReadClauseLiteral(string readRule, IReadOnlyList<FieldDefinition> fields, string rowAlias, string? userId)
    {
        if (string.IsNullOrWhiteSpace(readRule)) return null;

        var args = new List<object?>();
        var expression = Rewrite(readRule, fields, rowAlias, userId, null, null, args);
        return SlotToken().Replace(expression, m =>
        {
            var value = args[int.Parse(m.Groups["n"].Value)];
            return value is null ? "NULL" : $"'{value.ToString()!.Replace("'", "''")}'";
        });
    }

    private static string Slot(List<object?> args, object? value)
    {
        args.Add(value);
        return $"{{{args.Count - 1}}}";
    }

    private static object? Value(JsonObject? request, string name) =>
        request is not null && request.TryGetPropertyValue(name, out var node) && node is JsonValue v
            ? v.GetValue<object>() switch
            {
                System.Text.Json.JsonElement e => e.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
                    System.Text.Json.JsonValueKind.True => 1,
                    System.Text.Json.JsonValueKind.False => 0,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => e.ToString()
                },
                var other => other
            }
            : null;

    [GeneratedRegex("""(?<alias>_USER_|_ROW_|_REQ_)\s*\.\s*(?:"(?<quoted>[^"]+)"|(?<bare>[A-Za-z_][A-Za-z0-9_]*))""")]
    private static partial Regex AliasReference();

    [GeneratedRegex("""_[A-Z]+_""")]
    private static partial Regex UnknownAlias();

    [GeneratedRegex("""\{(?<n>\d+)\}""")]
    private static partial Regex SlotToken();
}
