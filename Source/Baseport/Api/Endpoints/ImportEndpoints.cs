using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/_admin/imports/preview", async (AppDbContext db, IHttpClientFactory clients, HttpContext ctx, JsonObject body) =>
        {
            var (connection, run, problem) = await ReadAsync(db, body);
            if (problem is not null) return problem;

            var (source, sourceError) = await DescribeAsync(db, connection!, run!, ctx.RequestAborted);
            if (sourceError is not null) return Results.BadRequest(new { errors = new[] { sourceError } });

            var (rows, error) = await FirstPageAsync(db, clients.CreateClient(), connection!, run!, ctx.RequestAborted, source?.Columns);
            if (error is not null) return Results.BadRequest(new { errors = new[] { error } });

            var table = TableFrom(Text(body, "tableName") ?? (source is null ? connection!.Name : source.Value.Table.Name), rows, source, run!);
            var existing = await db.Tables.Select(t => t.Name).ToListAsync();
            var settings = SqlSource.Settings(run!.ColumnsJson);
            return Results.Ok(new
            {
                table.Name,
                FirstPageRows = rows.Count,
                Fields = table.Fields.OrderBy(f => f.Position).Select(f => new { f.Name, f.Label, f.DataType, f.IsRequired }),
                Sample = rows.Take(5).Select(r => DefinitionImport.MapRow(r, table.Fields.ToList())),
                Columns = source?.Columns.Select(c => new
                {
                    c.Name,
                    c.SourceType,
                    c.FieldType,
                    c.IsKey,
                    Choice = settings.FirstOrDefault(s => s.Column == c.Name)?.Choice
                }),
                Errors = FieldValidation.ValidateTable(table, existing)
            });
        }).WithRequestTimeout(Timeouts.Long);

        app.MapPost("/api/_admin/imports", async (AppDbContext db, IHttpClientFactory clients, ImportRunner runner, HttpContext ctx, JsonObject body) =>
        {
            var (connection, run, problem) = await ReadAsync(db, body);
            if (problem is not null) return problem;

            var (source, sourceError) = await DescribeAsync(db, connection!, run!, ctx.RequestAborted);
            if (sourceError is not null) return Results.BadRequest(new { errors = new[] { sourceError } });
            if (source is { } described && SqlSource.UnchosenColumns(described.Columns, SqlSource.Settings(run!.ColumnsJson)) is { Count: > 0 } open)
                return Results.BadRequest(new { errors = new[] { $"Choose text or skip for the columns Baseport cannot map: {string.Join(", ", open)}." } });

            var (rows, error) = await FirstPageAsync(db, clients.CreateClient(), connection!, run!, ctx.RequestAborted, source?.Columns);
            if (error is not null) return Results.BadRequest(new { errors = new[] { error } });

            TableDefinition table;
            if (Text(body, "tableId") is { Length: > 0 } tableId)
            {
                var target = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tableId);
                if (target is null) return Results.NotFound();
                if (target.IsProxy) return Results.BadRequest(new { errors = new[] { "A proxy table stores nothing and cannot be filled." } });
                var writable = target.Fields.Where(f => !FieldTypes.Of(f).Computed).ToList();
                var probe = source is { } s ? [new JsonObject(s.Columns.Select(c => KeyValuePair.Create<string, JsonNode?>(c.Name, "")))] : rows;
                if (DefinitionImport.ColumnMap.For(probe, writable).MatchedFields.Count == 0)
                    return Results.BadRequest(new { errors = new[] { $"None of the API's fields ({string.Join(", ", rows.SelectMany(r => r.Select(p => p.Key)).Distinct().Take(8))}) match a field on this table." } });
                table = target;
            }
            else
            {
                table = TableFrom(Text(body, "tableName") ?? (source is null ? connection!.Name : source.Value.Table.Name), rows, source, run!);
                var existing = await db.Tables.Select(t => t.Name).ToListAsync();
                if (FieldValidation.ValidateTable(table, existing) is { Count: > 0 } tableErrors) return Results.BadRequest(new { errors = tableErrors });
                db.Tables.Add(table);
                await db.SaveChangesAsync();
                await RecordIndexes.SyncAsync(db, table);
            }

            run!.TableId = table.Id;
            db.ImportRuns.Add(run);
            await db.SaveChangesAsync();
            runner.Enqueue(run.Id);
            return Results.Ok(new { Table = ApiDtos.TableDto(table), Run = ImportRuns.Dto(run) });
        }).WithRequestTimeout(Timeouts.Long);

        app.MapGet("/api/_admin/imports/{id}", async (AppDbContext db, string id) =>
            await db.ImportRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id) is { } run ? Results.Ok(ImportRuns.Dto(run)) : Results.NotFound());

        app.MapGet("/api/_admin/imports", async (AppDbContext db, string? tableId) =>
        {
            var query = db.ImportRuns.AsNoTracking();
            if (!string.IsNullOrEmpty(tableId)) query = query.Where(r => r.TableId == tableId);
            return Results.Ok((await query.OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync()).Select(ImportRuns.Dto));
        });
    }

    private static async Task<(Connection? Connection, ImportRun? Run, IResult? Problem)> ReadAsync(AppDbContext db, JsonObject body)
    {
        var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == (Text(body, "connectionId") ?? ""));
        if (connection is null) return (null, null, Results.BadRequest(new { errors = new[] { "Choose a connection." } }));

        var paging = Text(body, "paging") ?? "auto";
        if (!RemoteFetch.Strategies.Contains(paging)) return (null, null, Results.BadRequest(new { errors = new[] { "Unknown paging strategy." } }));

        if (SqlSource.SettingsProblem(body["columns"], out var columns) is { } columnProblem)
            return (null, null, Results.BadRequest(new { errors = new[] { columnProblem } }));

        var run = new ImportRun
        {
            Id = Ids.NewShortId(12),
            ColumnsJson = columns,
            ConnectionId = connection.Id,
            Path = (Text(body, "path") ?? "").Trim(),
            Paging = paging,
            RecordsPointer = (Text(body, "recordsPointer") ?? "").Trim(),
            CreatedAt = DateTime.UtcNow
        };
        return (connection, run, null);
    }

    internal static async Task<(List<JsonObject> Rows, string? Error)> FirstPageAsync(
        AppDbContext db, HttpClient http, Connection connection, ImportRun run, CancellationToken ct,
        IReadOnlyList<SqlSource.SourceColumn>? columns = null)
    {
        var rows = new List<JsonObject>();
        var fieldsKnown = columns is not null;
        var settings = SqlSource.Settings(run.ColumnsJson);
        var probe = columns is null ? run : new ImportRun
        {
            ConnectionId = run.ConnectionId,
            Path = run.Path,
            ColumnsJson = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<SqlSource.ColumnSetting>>(
                [.. settings, .. SqlSource.UnchosenColumns(columns, settings).Select(c => new SqlSource.ColumnSetting(c, ColumnChoice.Skip))],
                SqlSource.JsonOptions)
        };
        try
        {
            await foreach (var page in ImportRuns.Pages(db, http, connection, probe, new RemoteFetch.Report(), ct, maxPages: 1))
                rows.AddRange(page.Records);
        }
        catch (RemoteFetch.FetchException ex)
        {
            return (rows, ex.Message);
        }
        return rows.Count == 0 && !fieldsKnown ? (rows, "The first page has no records to learn fields from.") : (rows, null);
    }

    private static async Task<((SqlSource.SourceTable Table, IReadOnlyList<SqlSource.SourceColumn> Columns)? Source, string? Error)> DescribeAsync(
        AppDbContext db, Connection connection, ImportRun run, CancellationToken ct)
    {
        if (!ConnectionProtocols.IsSql(connection.Protocol)) return (null, null);
        try
        {
            return (await SqlSource.DescribeAsync(db, connection, run.Path, ct), null);
        }
        catch (RemoteFetch.FetchException ex)
        {
            return (null, ex.Message);
        }
    }

    private static TableDefinition TableFrom(string name, IReadOnlyList<JsonObject> rows,
        (SqlSource.SourceTable Table, IReadOnlyList<SqlSource.SourceColumn> Columns)? source, ImportRun run)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = name.Trim(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var fields = source is { } s ? SqlSource.Fields(s.Columns, SqlSource.Settings(run.ColumnsJson)) : DefinitionImport.ToFields(DefinitionImport.InferFields(rows));
        foreach (var field in fields)
        {
            field.Id = Ids.NewShortId(12);
            field.TableId = table.Id;
            table.Fields.Add(field);
        }
        return table;
    }

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
