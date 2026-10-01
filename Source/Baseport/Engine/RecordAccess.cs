using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public enum Permission { Create, Read, Update, Delete }

public readonly record struct AccessCaller(string? Id, string? Role, string? Scope)
{
    public static AccessCaller Of(UserAccount account) =>
        new(account.Id, account.Role, account.Scope.Length == 0 ? null : account.Scope);
}

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

    internal static string EffectiveRule(string rule, string scopeField, Permission permission)
    {
        if (string.IsNullOrEmpty(scopeField)) return rule;
        var scope = $"{(permission == Permission.Create ? "_REQ_" : "_ROW_")}.\"{scopeField}\" = _USER_.scope";
        return string.IsNullOrWhiteSpace(rule) ? scope : $"({rule}) AND {scope}";
    }

    public static string? ScopeProblem(string scopeField, IReadOnlyList<FieldDefinition> fields)
    {
        if (scopeField.Length == 0) return null;
        var field = fields.FirstOrDefault(f => f.Name == scopeField);
        if (field is null) return $"The scope field '{scopeField}' does not name a field on this table.";
        return FieldValidation.NormalizeType(field.DataType) is "text" or "select" or "reference"
            ? null
            : "The scope field must be a text, select or reference field.";
    }

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

    public static async Task<string?> RuleProblemAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rule) =>
        Problem(rule, fields) ?? await SqlProblemAsync(db, table, fields, rule);

    public static async Task<string?> SqlProblemAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;

        var args = new List<object?>();
        var expression = Rewrite(rule, fields, "r", default, null, null, args);
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

    public static string? Problem(string? rule, IReadOnlyList<FieldDefinition> fields)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        if (rule.Length > 2000)
            return "An access rule must be 2000 characters or fewer.";
        if (rule.Contains(';'))
            return "An access rule is a single expression and cannot contain ';'.";
        if (ShapeProblem(rule) is { } shape)
            return shape;

        foreach (Match match in AliasReference().Matches(rule))
        {
            var alias = match.Groups["alias"].Value;
            var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;

            if (alias == "_USER_")
            {
                if (name is not ("id" or "role" or "scope"))
                    return $"_USER_ has no '{name}'. Only _USER_.id, _USER_.role and _USER_.scope are available.";
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

    // the rule must stay one parenthesised expression
    private static string? ShapeProblem(string rule)
    {
        if (rule.IndexOfAny(['{', '}']) >= 0)
            return "An access rule cannot contain '{' or '}'.";

        var depth = 0;
        for (var i = 0; i < rule.Length; i++)
        {
            var c = rule[i];
            if (c is '\'' or '"' or '`' or '[')
            {
                var end = rule.IndexOf(c == '[' ? ']' : c, i + 1);
                if (end < 0) return "An access rule has a quote or bracket that is never closed.";
                if (c == '\'' && AliasReference().IsMatch(rule[(i + 1)..end]))
                    return "_USER_, _ROW_ and _REQ_ cannot appear inside a quoted string.";
                i = end;
            }
            else if (c is '?' or '$' or ':' or '@')
                return $"An access rule cannot contain '{c}' outside a quoted string.";
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return "An access rule has a ')' without a matching '('.";
            else if ((c == '-' || c == '/') && i + 1 < rule.Length && rule[i + 1] == (c == '-' ? '-' : '*'))
                return "An access rule cannot contain a comment.";
        }
        return depth == 0 ? null : "An access rule has a '(' that is never closed.";
    }

    internal static string Rewrite(string rule, IReadOnlyList<FieldDefinition> fields, string? rowAlias, AccessCaller caller, JsonObject? request, JsonObject? row, List<object?> args)
    {
        return AliasReference().Replace(rule, match => $"({Substitute(match)})");

        string Substitute(Match match)
        {
            var alias = match.Groups["alias"].Value;
            var name = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;

            if (alias == "_USER_")
                return Slot(args, name switch { "role" => caller.Role, "scope" => caller.Scope, _ => caller.Id });
            if (alias == "_REQ_")
                return Slot(args, Value(request, name));

            var field = fields.FirstOrDefault(f => f.Name == name);
            if (field is null)
                return "NULL";
            if (rowAlias is not null)
                return QueryEngine.JsonPathFor(field.Name, rowAlias);

            return row is null ? "NULL" : Slot(args, Value(row, name));
        }
    }

    public static async Task<bool> AllowsAsync(
        AppDbContext db,
        TableDefinition table,
        IReadOnlyList<FieldDefinition> fields,
        Permission permission,
        AccessCaller caller,
        string? recordId = null,
        JsonObject? request = null,
        JsonObject? row = null)
    {
        var rule = EffectiveRule(RuleFor(table, permission), table.ScopeField, permission);
        return string.IsNullOrWhiteSpace(rule) || await EvaluateAsync(db, table, fields, rule, caller, recordId, request, row);
    }

    private static async Task<bool> EvaluateAsync(
        AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rule, AccessCaller caller,
        string? recordId, JsonObject? request, JsonObject? row)
    {
        var args = new List<object?>();
        var fromRow = recordId is not null;
        var expression = Rewrite(rule, fields, fromRow ? "r" : null, caller, request, row, args);

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

        var results = await db.Database.SqlQueryRaw<int>(sql, args.Select(a => a ?? DBNull.Value).ToArray()).ToListAsync();
        return results.Count > 0 && results[0] != 0;
    }

    public static async Task<string?> FieldRuleProblemAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, FieldDefinition field)
    {
        if (!string.IsNullOrWhiteSpace(field.ReadRule) && AliasReference().Matches(field.ReadRule).Any(m => m.Groups["alias"].Value == "_REQ_"))
            return $"The read rule of '{field.Name}' cannot refer to _REQ_: a read has no request body.";
        foreach (var (kind, rule) in new[] { ("read", field.ReadRule), ("write", field.WriteRule) })
            if (await RuleProblemAsync(db, table, fields, rule.Trim()) is { } problem)
                return $"The {kind} rule of '{field.Name}': {problem}";
        return null;
    }

    public static async Task<IReadOnlyList<Record>> RedactAsync(
        AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, AccessCaller caller, IReadOnlyList<Record> records,
        CancellationToken token = default)
    {
        var guarded = fields.Where(f => !string.IsNullOrWhiteSpace(f.ReadRule)).ToList();
        if (guarded.Count == 0 || records.Count == 0) return records;

        var hidden = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var field in guarded)
        {
            var args = new List<object?>();
            var expression = Rewrite(field.ReadRule, fields, "r", caller, null, null, args);
            var ids = string.Join(", ", records.Select(r => Slot(args, r.Id)));
            var sql = $"""
                SELECT r."Id" AS "Value" FROM "_records" r
                WHERE r."TableId" = {Slot(args, table.Id)} AND r."Id" IN ({ids}) AND COALESCE(CAST(({expression}) AS INTEGER), 0) = 0
                """;
            foreach (var id in await db.Database.SqlQueryRaw<string>(sql, args.Select(a => a ?? DBNull.Value).ToArray()).ToListAsync(token))
            {
                if (!hidden.TryGetValue(id, out var names)) hidden[id] = names = [];
                names.Add(field.Name);
            }
        }
        return records.Select(r => hidden.TryGetValue(r.Id, out var names) ? Without(r, names) : r).ToList();
    }

    public static async Task<JsonObject> RedactRowAsync(
        AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, AccessCaller caller, JsonObject row)
    {
        foreach (var field in fields.Where(f => !string.IsNullOrWhiteSpace(f.ReadRule)))
            if (!await EvaluateAsync(db, table, fields, field.ReadRule, caller, null, null, row))
                row.Remove(field.Name);
        return row;
    }

    public static async Task<string?> WriteRefusalAsync(
        AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> fields, AccessCaller caller, JsonObject request,
        string? recordId = null, bool replace = false)
    {
        foreach (var field in fields.Where(f => !string.IsNullOrWhiteSpace(f.WriteRule)))
            if ((replace || request.ContainsKey(field.Name))
                && !await EvaluateAsync(db, table, fields, field.WriteRule, caller, recordId, request, null))
                return $"You may not set '{field.Name}'.";
        return null;
    }

    private static Record Without(Record record, List<string> names)
    {
        var data = JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject ?? new JsonObject();
        foreach (var name in names) data.Remove(name);
        return new Record { Id = record.Id, TableId = record.TableId, JsonData = data.ToJsonString(), CreatedAt = record.CreatedAt, UpdatedAt = record.UpdatedAt };
    }

    public static string? ListClause(TableDefinition table, IReadOnlyList<FieldDefinition> fields, string rowAlias, AccessCaller caller, List<object> args)
    {
        var rule = EffectiveRule(table.ReadRule, table.ScopeField, Permission.Read);
        if (string.IsNullOrWhiteSpace(rule)) return null;

        var collected = new List<object?>();
        var expression = Rewrite(rule, fields, rowAlias, caller, null, null, collected);

        var offset = args.Count;
        args.AddRange(collected.Select(a => a ?? (object)DBNull.Value));
        return SlotToken().Replace(expression, m => $"{{{int.Parse(m.Groups["n"].Value) + offset}}}");
    }

    public static string? ReadClauseLiteral(string readRule, string scopeField, IReadOnlyList<FieldDefinition> fields, string rowAlias, AccessCaller caller)
    {
        var rule = EffectiveRule(readRule, scopeField, Permission.Read);
        if (string.IsNullOrWhiteSpace(rule)) return null;

        var args = new List<object?>();
        var expression = Rewrite(rule, fields, rowAlias, caller, null, null, args);
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
