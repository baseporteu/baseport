using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Server-rendered table bodies for the console.
public static class FragmentEndpoints
{
    private const string Fragment = "text/html; charset=utf-8";

    public static void MapFragmentEndpoints(this WebApplication app)
    {
        // The tables index. Sorted in memory: the counts aren't real columns, and this list is admin-scale, never records-scale.
        app.MapGet("/api/_admin/fragments/tables", async (AppDbContext db, string? sort, string? order) =>
        {
            var tables = await db.Tables.Include(t => t.Fields).ToListAsync();
            var formCounts = await db.FormConfigs.GroupBy(f => f.TableId)
                .Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            var recordCounts = await db.Records.GroupBy(r => r.TableId)
                .Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            // real bytes of stored JSON, not the on-disk page size, but an honest proxy for "how much does this weigh"
            var dataBytes = await db.Records.GroupBy(r => r.TableId)
                .Select(g => new { TableId = g.Key, Bytes = g.Sum(r => (long)r.JsonData.Length) }).ToListAsync();

            long DataSize(TableDefinition t) => dataBytes.FirstOrDefault(d => d.TableId == t.Id)?.Bytes ?? 0;
            long IndexSize(TableDefinition t) => RecordIndexes.EstimateIndexBytes(t, recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0);

            IEnumerable<TableDefinition> ordered = (sort ?? "name").ToLowerInvariant() switch
            {
                "fields" => tables.OrderBy(t => t.Fields.Count),
                "forms" => tables.OrderBy(t => formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0),
                "records" => tables.OrderBy(t => recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0),
                "api" => tables.OrderBy(t => t.ApiEnabled),
                "datasize" => tables.OrderBy(DataSize),
                "indexsize" => tables.OrderBy(IndexSize),
                _ => tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            };
            if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) ordered = ordered.Reverse();

            var html = new StringBuilder();
            foreach (var t in ordered)
            {
                // The API column already states exposure, only proxy gets a badge.
                var badges = t.IsProxy ? Html.Badge("proxy") : "";

                var name = $"<strong>{Html.Text(t.Name)}</strong> {badges}" +
                           (t.Description.Length > 0 ? $"<div class=\"muted\">{Html.Text(t.Description)}</div>" : "");

                html.Append($"<tr class=\"row-link\" onclick=\"navigate('/tables/{Html.Text(Html.JsString(t.Id))}')\">")
                    .Append(Html.RawCell(name))
                    .Append(Html.RawCell(Html.Num(t.Fields.Count)))
                    .Append(Html.RawCell(Html.Num(formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0)))
                    // A proxy table stores nothing here, a count would be a lie.
                    .Append(Html.RawCell(t.IsProxy ? Html.Muted("∞") : Html.Num(recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0)))
                    .Append(Html.RawCell(t.ApiEnabled ? Html.Text(t.ApiName) : Html.Muted("Off")))
                    .Append(Html.RawCell(t.IsProxy ? Html.Muted("-") : Html.BytesHtml(DataSize(t))))
                    .Append(Html.RawCell(t.IsProxy ? Html.Muted("-") :
                        $"<span class=\"muted\" title=\"Estimated from row and index-column counts; SQLite's dbstat isn't compiled into this build, this isn't an exact measurement.\">~{Html.BytesHtml(IndexSize(t))}</span>"))
                    .Append(Html.RawCell($"<div class=\"field-row-actions\"><button class=\"btn btn-ghost btn-sm\" onclick=\"event.stopPropagation(); navigate('/tables/{Html.Text(Html.JsString(t.Id))}')\">Open</button></div>"))
                    .Append("</tr>");
            }
            return Results.Text(html.ToString(), Fragment);
        });

        // One table's records, paged and searched.
        app.MapGet("/api/_admin/fragments/records/{tableId}", async (AppDbContext db, HttpContext ctx, string tableId,
                                                             string? q, string? sort, string? order, int? page, int? pageSize) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tableId);
            if (table == null) return Results.NotFound();

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).Where(f => !f.IsHidden).ToList();
            // Names the row in the delete confirmation. A lookup identifier is the closest thing a table has to a primary key an author would recognise, and validation already refuses to let one be hidden.
            var identifier = fields.FirstOrDefault(f => f.IsIdentifier);
            var sortField = fields.FirstOrDefault(f => f.Name == sort);
            var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

            var result = await QueryEngine.ListAsync(db, table, Array.Empty<FieldDefinition>(), sortField, descending, q, page ?? 1, pageSize ?? 25);

            var html = new StringBuilder();
            foreach (var record in result.Records)
            {
                var data = JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject ?? new JsonObject();
                html.Append("<tr>");
                foreach (var f in fields)
                    html.Append(Html.Cell(Html.DisplayValue(data[f.Name])));
                html.Append(Html.Cell(record.CreatedAt.ToLocalTime(), "muted"))
                    .Append(Html.Cell(record.UpdatedAt.ToLocalTime(), "muted"))
                    .Append(Html.RawCell(Html.Button("Delete", "deleteRecord", record.Id, Html.Shorten(identifier is null ? "" : Html.DisplayValue(data[identifier.Name])))))
                    .Append("</tr>");
            }

            Paging(ctx, result.Page, result.PageSize, result.Total, result.TotalPages, result.HasMore, result.CountExact);
            return Results.Text(html.ToString(), Fragment);
        });

        // Every form across every table.
        app.MapGet("/api/_admin/fragments/forms", async (AppDbContext db, HttpContext ctx, string? kind, string? sort, string? order) =>
        {
            var tables = await db.Tables.ToDictionaryAsync(t => t.Id);
            var forms = await db.FormConfigs.ToListAsync();
            if (!string.IsNullOrWhiteSpace(kind))
                forms = forms.Where(f => f.Kind == FormKinds.Normalize(kind)).ToList();

            // Id is a random string, not a timestamp; CreatedAt descending is the real "newest first"
            IEnumerable<FormConfig> ordered;
            if (string.IsNullOrEmpty(sort))
            {
                ordered = forms.OrderByDescending(f => f.CreatedAt);
            }
            else
            {
                ordered = sort.ToLowerInvariant() switch
                {
                    "title" => forms.OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase),
                    "kind" => forms.OrderBy(f => f.Kind, StringComparer.OrdinalIgnoreCase),
                    "table" => forms.OrderBy(f => tables.GetValueOrDefault(f.TableId)?.Name ?? "", StringComparer.OrdinalIgnoreCase),
                    "status" => forms.OrderBy(f => f.IsPublished),
                    _ => forms.OrderByDescending(f => f.CreatedAt),
                };
                if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) ordered = ordered.Reverse();
            }

            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var html = new StringBuilder();
            foreach (var f in ordered)
            {
                var embed = $"<script src=\"{origin}/embed.js?id={f.Id}\"></script>";
                var actions = string.Join(" ", FormActions.Parse(f.Actions).Select(Html.Tag));

                html.Append($"<tr class=\"row-link\" onclick=\"navigate('/forms/{Html.Text(Html.JsString(f.Id))}')\">")
                    .Append(Html.RawCell($"<strong>{Html.Text(f.Title.Length > 0 ? f.Title : "Untitled form")}</strong>" +
                                         (f.Description.Length > 0 ? $"<div class=\"muted\">{Html.Text(f.Description)}</div>" : "")))
                    .Append(Html.RawCell(Html.Badge(f.Kind == FormKinds.List ? "List" : "Form") + " " + (f.Kind == FormKinds.List ? "" : actions)))
                    .Append(Html.Cell(tables.GetValueOrDefault(f.TableId)?.Name ?? "-"))
                    .Append(Html.RawCell(f.IsPublished ? Html.Badge("published") : Html.Badge("draft", "badge-required")))
                    .Append(Html.RawCell($"<input class=\"input embed-input\" type=\"text\" readonly value=\"{Html.Text(embed)}\" onclick=\"this.select()\">"))
                    .Append(Html.RawCell($"<div class=\"field-row-actions\"><button class=\"btn btn-ghost btn-sm\" onclick=\"event.stopPropagation(); navigate('/forms/{Html.Text(Html.JsString(f.Id))}')\">Open</button></div>"))
                    .Append("</tr>");
            }
            return Results.Text(html.ToString(), Fragment);
        });

        // User accounts.
        app.MapGet("/api/_admin/fragments/accounts", async (AppDbContext db, HttpContext ctx, string? q, int? page, int? pageSize) =>
        {
            var accounts = await db.UserAccounts.OrderBy(u => u.Username).ToListAsync();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                accounts = accounts.Where(a =>
                    a.Username.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    a.Email.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            var current = Math.Max(1, page ?? 1);
            var total = accounts.Count;
            var slice = accounts.Skip((current - 1) * size).Take(size).ToList();

            var html = new StringBuilder();
            foreach (var a in slice)
            {
                html.Append("<tr>")
                    .Append(Html.Cell(a.Id.Length > 8 ? a.Id[..8] + "…" : a.Id, "mono-id"))
                    .Append(Html.RawCell($"<strong>{Html.Text(a.Username)}</strong>"))
                    .Append(Html.RawCell(a.Email.Length > 0 ? Html.Text(a.Email) : Html.Muted("-"), "muted"))
                    .Append(Html.RawCell(a.Role == AccountRoles.Admin ? Html.Tag("admin") : Html.Muted("consumer")))
                    .Append(Html.RawCell(AccessState(a)))
                    .Append(Html.Cell(a.UpdatedAt.ToLocalTime(), "muted"))
                    .Append(Html.Cell(a.CreatedAt.ToLocalTime(), "muted"))
                    .Append(Html.RawCell(Html.IconButton(PencilIcon, "Edit", "openAccountForm", a.Id), "row-actions"))
                    .Append("</tr>");
            }

            Paging(ctx, current, size, total, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
            return Results.Text(html.ToString(), Fragment);
        });

        // The activity log.
        app.MapGet("/api/_admin/fragments/logs", async (AppDbContext db, HttpContext ctx, string? filter, string? sort,
                                                 string? order, int? page, int? perPage) =>
        {
            var size = Math.Clamp(perPage ?? 50, 1, 100);
            var current = Math.Max(1, page ?? 1);

            IQueryable<AuditLog> query = db.AuditLogs;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim().ToLowerInvariant();
                query = query.Where(l => l.Method.ToLower().Contains(f) || l.Path.ToLower().Contains(f) || l.TableName.ToLower().Contains(f));
            }

            var total = await query.CountAsync();

            // Column headers in the view offer sortable fields; nothing reaches SQL without passing through this whitelist.
            var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            IQueryable<AuditLog> ordered = (sort, descending) switch
            {
                ("createdAt", false) => query.OrderBy(l => l.CreatedAt),
                ("createdAt", true) => query.OrderByDescending(l => l.CreatedAt),
                ("method", false) => query.OrderBy(l => l.Method),
                ("method", true) => query.OrderByDescending(l => l.Method),
                ("path", false) => query.OrderBy(l => l.Path),
                ("path", true) => query.OrderByDescending(l => l.Path),
                ("status", false) => query.OrderBy(l => l.Status),
                ("status", true) => query.OrderByDescending(l => l.Status),
                ("tableName", false) => query.OrderBy(l => l.TableName),
                ("tableName", true) => query.OrderByDescending(l => l.TableName),
                ("message", false) => query.OrderBy(l => l.Message),
                ("message", true) => query.OrderByDescending(l => l.Message),
                _ => query.OrderByDescending(l => l.CreatedAt)
            };

            var logs = await ordered.Skip((current - 1) * size).Take(size).ToListAsync();

            var html = new StringBuilder();
            foreach (var l in logs)
            {
                html.Append("<tr>")
                    .Append(Html.Cell(l.CreatedAt.ToLocalTime(), "muted"))
                    .Append(Html.RawCell($"<code>{Html.Text(l.Method)}</code>"))
                    .Append(Html.RawCell($"<code>{Html.Text(l.Path)}</code>"))
                    // A failed request is what an operator scans for, and a script that died in the browser is one, even though it never had a status.
                    .Append(l.Status >= 400 || l.Method == ClientErrorEndpoints.ClientMethod
                        ? $"<td style=\"color:#d63d3d\">{Html.Text(l.Status > 0 ? l.Status.ToString() : "-")}</td>"
                        : Html.Cell(l.Status))
                    .Append(Html.Cell(l.TableName.Length > 0 ? l.TableName : "-", "muted"))
                    .Append(Html.Cell(l.Message.Length > 0 ? l.Message : "-", "muted"))
                    .Append("</tr>");
            }

            Paging(ctx, current, size, total, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
            return Results.Text(html.ToString(), Fragment);
        });

        // A query result: the whole table, rendered.
        app.MapPost("/api/_admin/fragments/sql", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var sql = body["sql"]?.GetValue<string>() ?? "";
            var queryId = body["queryId"]?.GetValue<string>();

            SavedQuery? saved = null;
            if (!string.IsNullOrWhiteSpace(queryId))
            {
                saved = await db.SavedQueries.FirstOrDefaultAsync(q => q.Id == queryId);
                if (saved is not null) sql = saved.Sql;
            }

            // Validated here as well as in the JSON endpoint: this is a separate route to the same connection, it cannot lean on that check.
            var invalid = SqlEngine.Validate(sql);
            if (invalid is not null) return Results.BadRequest(new { errors = new[] { invalid } });

            var result = await SqlEngine.ReadAsync(db, sql, WireCatalog.Views, restrict: false);
            if (result.Error is not null) return Results.BadRequest(new { errors = new[] { result.Error } });

            if (saved is not null)
            {
                saved.LastExecutedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            ctx.Response.Headers["X-Row-Count"] = result.Rows.Count.ToString();
            ctx.Response.Headers["X-Truncated"] = result.Truncated ? "1" : "0";
            ctx.Response.Headers["X-Column-Count"] = result.Columns.Count.ToString();

            if (result.Columns.Count == 0) return Results.Text("", Fragment);

            var html = new StringBuilder("<div class=\"table-wrap\"><table class=\"table\"><thead><tr>");
            foreach (var c in result.Columns) html.Append($"<th>{Html.Text(c)}</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var row in result.Rows)
            {
                html.Append("<tr>");
                foreach (var value in row)
                    // NULL is shown as a value, not an empty cell, it reads differently from a stored empty string.
                    html.Append(value is null ? Html.RawCell(Html.Muted("NULL")) : Html.Cell(value));
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div>");
            return Results.Text(html.ToString(), Fragment);
        });
    }

    // Paging travels in headers because the body is markup, not an envelope.
    private static void Paging(HttpContext ctx, int page, int pageSize, int total, int totalPages, bool hasMore = false, bool countExact = true)
    {
        ctx.Response.Headers["X-Page"] = page.ToString();
        ctx.Response.Headers["X-Page-Size"] = pageSize.ToString();
        ctx.Response.Headers["X-Total"] = total.ToString();
        ctx.Response.Headers["X-Total-Pages"] = totalPages.ToString();
        // Past the count ceiling X-Total is a floor, the pager needs a separate answer to "is there a next page" or it stops at the counted range.
        ctx.Response.Headers["X-Has-More"] = hasMore ? "1" : "0";
        ctx.Response.Headers["X-Count-Exact"] = countExact ? "1" : "0";
    }

    private static string AccessState(UserAccount a)
    {
        if (a.IsDisabled) return Html.Tag("disabled");
        if (!a.ApiEnabled) return Html.Muted("no API");
        if (a.ApiTokenExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return Html.Tag("token expired");
        return Html.Tag("API") + " " + Html.Muted($"until {a.ApiTokenExpiresAt?.ToLocalTime():d}");
    }

    private const string PencilIcon =
        "<svg width=\"13\" height=\"13\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
        "stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z\"/></svg>";
}
