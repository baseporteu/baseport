using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json.Nodes;

namespace Baseport;

// Accounts, API switch, SQL console, saved queries, logs, settings.
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        static object JobDto(JobConfig j) => new
        {
            j.Key, j.Name, j.Schedule, j.Enabled,
            NextRunAt = Utc(j.NextRunAt),
            LastRunAt = Utc(j.LastRunAt),
            j.LastResult
        };
        static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);

        app.MapGet("/api/_admin/accounts", async (AppDbContext db) =>
        {
            var accounts = await db.UserAccounts.OrderBy(u => u.Id).ToListAsync();
            // The token itself is returned once, when it is generated, and never again.
            return Results.Ok(accounts.Select(a => new
            {
                a.Id, a.Username, a.Email, a.Role, a.IsDisabled,
                a.CreatedAt, a.UpdatedAt, a.LastLoginAt,
                a.ApiEnabled, a.ApiTokenExpiresAt,
                HasApiToken = !string.IsNullOrEmpty(a.ApiTokenHash),
                ApiTokenExpired = a.ApiTokenExpiresAt is { } e && e <= DateTime.UtcNow
            }));
        });

        app.MapPost("/api/_admin/accounts", async (AppDbContext db, JsonObject body) =>
        {
            var username = body["username"] is JsonValue uv && uv.TryGetValue<string>(out var u) ? u.Trim() : "";
            var email = body["email"] is JsonValue ev && ev.TryGetValue<string>(out var em) ? em.Trim() : "";
            var errors = AccountValidation.Validate(username, email);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors, invalid = InvalidAccountFields(errors) });
            if (await db.UserAccounts.AnyAsync(a => a.Username == username))
                return Results.BadRequest(new { errors = new[] { "Username already exists." }, invalid = new[] { "username" } });
            if (!string.IsNullOrWhiteSpace(email) && await db.UserAccounts.AnyAsync(a => a.Email == email))
                return Results.BadRequest(new { errors = new[] { "That email is already on another account." }, invalid = new[] { "email" } });
            var role = body["role"] is JsonValue av && av.TryGetValue<string>(out var r) ? AccountRoles.Normalize(r) : AccountRoles.Consumer;
            if (role is null) return Results.BadRequest(new { errors = new[] { "Role must be admin, consumer or user." } });
            var now = DateTime.UtcNow;
            var account = new UserAccount
            {
                Id = Ids.NewShortId(12),
                Username = username,
                Email = email,
                CreatedAt = now,
                UpdatedAt = now,
                Role = role,
                ApiTokenHash = "",
                ApiEnabled = false
            };
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                account.Id, account.Username, account.Email, account.Role, account.IsDisabled,
                account.CreatedAt, account.UpdatedAt, account.LastLoginAt,
                account.ApiEnabled, account.ApiTokenExpiresAt,
                HasApiToken = !string.IsNullOrEmpty(account.ApiTokenHash)
            });
        });

        app.MapPatch("/api/_admin/accounts/{pid}", async (AppDbContext db, string pid, JsonObject body) =>
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == pid);
            if (account == null) return Results.NotFound();
            // Console access alone must never be enough to take another operator's account over, so on an admin the console may not touch what would: the password, the role, the disabled switch, and deletion. The name and the address are neither. Baseport sends no mail, so there is no reset path behind an address, and pillar 17 already refuses to auto-link an admin by either.
            var locked = account.Role == AccountRoles.Admin;
            // Validate the resulting account, not just the supplied keys: a PATCH that sets only an e-mail must still be checked against the rules.
            var nextUsername = body["username"] is JsonValue uv && uv.TryGetValue<string>(out var uname) ? uname.Trim() : account.Username;
            var nextEmail = body["email"] is JsonValue ev && ev.TryGetValue<string>(out var mail) ? mail.Trim() : account.Email;

            var errors = AccountValidation.Validate(nextUsername, nextEmail);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors, invalid = InvalidAccountFields(errors) });
            if (await db.UserAccounts.AnyAsync(a => a.Username == nextUsername && a.Id != account.Id))
                return Results.BadRequest(new { errors = new[] { "Username already exists." }, invalid = new[] { "username" } });
            if (!string.IsNullOrWhiteSpace(nextEmail) && await db.UserAccounts.AnyAsync(a => a.Email == nextEmail && a.Id != account.Id))
                return Results.BadRequest(new { errors = new[] { "That email is already on another account." }, invalid = new[] { "email" } });

            account.Username = nextUsername;
            account.Email = nextEmail;
            if (body["role"] is JsonValue av && av.TryGetValue<string>(out var rawRole))
            {
                var role = AccountRoles.Normalize(rawRole);
                if (role is null) return Results.BadRequest(new { errors = new[] { "Role must be admin, consumer or user." } });
                // Promotion is the console taking on a privilege it cannot be trusted to grant itself; demotion is how one operator removes another. Both belong to whoever has the shell.
                if (role == AccountRoles.Admin || locked)
                    return Results.BadRequest(new { errors = new[] { AdminOnlyByCli } });
                account.Role = role;
            }

            // A password an operator sets for somebody else is a one-time credential: the owner replaces it on first use, and every session opened under the old one is gone.
            if (body["password"] is JsonValue pv && pv.TryGetValue<string>(out var newPassword) && !string.IsNullOrEmpty(newPassword))
            {
                if (locked) return Results.BadRequest(new { errors = new[] { AdminOnlyByCli } });
                if (AccountValidation.PasswordProblem(newPassword) is { } passwordProblem)
                    return Results.BadRequest(new { errors = new[] { passwordProblem }, invalid = new[] { "password" } });

                account.PasswordHash = AdminAuth.HashPassword(newPassword);
                account.MustChangePassword = true;
                await UserTokens.RevokeAllAsync(db, account.Id);
            }

            if (body["isDisabled"] is JsonValue dv2 && dv2.TryGetValue<bool>(out var disabled) && disabled != account.IsDisabled)
            {
                // Locking another operator out is the same takeover by a different door.
                if (locked) return Results.BadRequest(new { errors = new[] { AdminOnlyByCli } });
                // Disabling the last account that can sign in locks the console.
                if (disabled && !account.IsDisabled && await db.UserAccounts.CountAsync(a => !a.IsDisabled && a.Id != account.Id) == 0)
                    return Results.BadRequest(new { errors = new[] { "This is the last enabled account and cannot be disabled." } });

                account.IsDisabled = disabled;
                // A disabled account must not keep a live session on either surface.
                if (disabled) await UserTokens.RevokeAllAsync(db, account.Id);
            }

            if (body["apiEnabled"] is JsonValue ae && ae.TryGetValue<bool>(out var apiEnabled))
            {
                if (apiEnabled && string.IsNullOrEmpty(account.ApiTokenHash))
                    return Results.BadRequest(new { errors = new[] { "Generate a token before enabling API access." } });
                if (apiEnabled && account.ApiTokenExpiresAt is null)
                    return Results.BadRequest(new { errors = new[] { "An enabled token must have an expiry date." } });
                account.ApiEnabled = apiEnabled;
            }

            if (body.ContainsKey("apiTokenExpiresAt"))
            {
                var raw = body["apiTokenExpiresAt"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (account.ApiEnabled)
                        return Results.BadRequest(new { errors = new[] { "An enabled token must have an expiry date." } });
                    account.ApiTokenExpiresAt = null;
                }
                else if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var expiry))
                {
                    return Results.BadRequest(new { errors = new[] { "Token expiry is not a valid date." } });
                }
                else if (expiry <= DateTime.UtcNow)
                {
                    return Results.BadRequest(new { errors = new[] { "Token expiry must be in the future." } });
                }
                else
                {
                    account.ApiTokenExpiresAt = expiry;
                }
            }
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                account.Id, account.Username, account.Email, account.Role, account.IsDisabled,
                account.CreatedAt, account.UpdatedAt, account.LastLoginAt,
                account.ApiEnabled, account.ApiTokenExpiresAt,
                HasApiToken = !string.IsNullOrEmpty(account.ApiTokenHash)
            });
        });

        app.MapDelete("/api/_admin/accounts/{pid}", async (AppDbContext db, HttpContext ctx, string pid) =>
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == pid);
            if (account == null) return Results.NotFound();
            if (account.Role == AccountRoles.Admin)
                return Results.BadRequest(new { errors = new[] { AdminOnlyByCli } });

            // Deleting the last account that can still sign in locks everyone out of the console permanently, with no way back in.
            var otherEnabled = await db.UserAccounts.CountAsync(a => a.Id != account.Id && !a.IsDisabled);
            if (otherEnabled == 0)
                return Results.BadRequest(new { errors = new[] { "This is the last enabled account. Enable another before deleting it." } });

            await UserTokens.RevokeAllAsync(db, account.Id);
            db.UserAccounts.Remove(account);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = account.Id });
        });

        // Rotating a token is per account: the credential belongs to the caller that uses it, so revoking one must not revoke everyone else's.
        app.MapPost("/api/_admin/accounts/{pid}/token", async (AppDbContext db, string pid, JsonObject body) =>
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == pid);
            if (account == null) return Results.NotFound();

            var raw = body["expiresAt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { errors = new[] { "Choose an expiry date for the token." } });
            if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var expiresAt))
                return Results.BadRequest(new { errors = new[] { "Token expiry is not a valid date." } });
            if (expiresAt <= DateTime.UtcNow)
                return Results.BadRequest(new { errors = new[] { "Token expiry must be in the future." } });
            if (expiresAt > DateTime.UtcNow.AddYears(10))
                return Results.BadRequest(new { errors = new[] { "Token expiry cannot be more than ten years away." } });

            var token = Ids.NewShortId(48);
            account.ApiTokenHash = ApiAuth.HashToken(token);
            account.ApiEnabled = true;
            // Stored to the end of the chosen day, so a token picked for "today" is not already dead.
            account.ApiTokenExpiresAt = expiresAt.TimeOfDay == TimeSpan.Zero ? expiresAt.AddDays(1).AddSeconds(-1) : expiresAt;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // The only moment the token is ever returned: only its hash is kept, so nothing can read it back afterwards.
            return Results.Ok(new { apiToken = token, expiresAt = account.ApiTokenExpiresAt });
        });

        app.MapDelete("/api/_admin/accounts/{pid}/token", async (AppDbContext db, string pid) =>
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == pid);
            if (account == null) return Results.NotFound();
            account.ApiTokenHash = "";
            account.ApiEnabled = false;
            account.ApiTokenExpiresAt = null;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { revoked = true });
        });

        // Per-table switch for the public REST API.
        app.MapPut("/api/_admin/tables/{pid}/api", async (AppDbContext db, string pid, JsonObject body) =>
        {
            var enabled = body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e;
            var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == pid);
            if (table == null) return Results.NotFound();
            if (enabled && string.IsNullOrWhiteSpace(table.ApiName))
                return Results.BadRequest(new { errors = new[] { "Give this table an API name before publishing it." } });
            table.ApiEnabled = enabled;
            await db.SaveChangesAsync();
            return Results.Ok(new { apiEnabled = table.ApiEnabled });
        });

        app.MapGet("/api/_admin/logs", async (AppDbContext db, string? filter, string? sort, string? order, int page, int perPage) =>
        {
            page = page < 1 ? 1 : page;
            perPage = perPage < 1 ? 50 : (perPage > 100 ? 100 : perPage);
            IQueryable<AuditLog> query = db.AuditLogs;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                query = query.Where(l => l.Method.Contains(f) || l.Path.Contains(f) ||
                                         (l.TableName != null && l.TableName.Contains(f)) ||
                                         (l.Message != null && l.Message.Contains(f)));
            }
            var sortKey = (sort ?? "createdAt").Trim().ToLowerInvariant();
            var desc = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase) ? false : true;
            query = sortKey switch
            {
                "method" => desc ? query.OrderByDescending(l => l.Method) : query.OrderBy(l => l.Method),
                "path" => desc ? query.OrderByDescending(l => l.Path) : query.OrderBy(l => l.Path),
                "status" => desc ? query.OrderByDescending(l => l.Status) : query.OrderBy(l => l.Status),
                "tablename" => desc ? query.OrderByDescending(l => l.TableName) : query.OrderBy(l => l.TableName),
                "message" => desc ? query.OrderByDescending(l => l.Message) : query.OrderBy(l => l.Message),
                _ => desc ? query.OrderByDescending(l => l.CreatedAt) : query.OrderBy(l => l.CreatedAt)
            };
            var total = await query.CountAsync();
            var logs = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync();
            return Results.Ok(new
            {
                total,
                page,
                perPage,
                logs = logs.Select(l => new { l.Id, l.CreatedAt, l.Method, l.Path, l.Status, l.TableName, l.Message })
            });
        });

        // Scheduled maintenance jobs: read schedules, edit cron/enabled, trigger a one-off run.
        app.MapGet("/api/_admin/jobs", async (AppDbContext db) =>
        {
            var jobs = await db.JobConfigs.OrderBy(j => j.Name).ToListAsync();
            return Results.Ok(jobs.Select(JobDto));
        });

        app.MapPut("/api/_admin/jobs/{key}", async (AppDbContext db, string key, JsonObject body) =>
        {
            var job = await db.JobConfigs.FirstOrDefaultAsync(j => j.Key == key);
            if (job is null) return Results.NotFound(new { errors = new[] { "Unknown job." } });
            if (body["schedule"] is JsonValue sv && sv.TryGetValue<string>(out var schedule))
            {
                var cron = schedule.Trim();
                var err = Jobs.Validate(cron);
                if (err != null) return Results.BadRequest(new { errors = new[] { err } });
                job.Schedule = cron;
                job.NextRunAt = Jobs.NextRun(job.Schedule, DateTime.UtcNow) ?? DateTime.UtcNow.AddHours(1);
            }
            if (body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled))
                job.Enabled = enabled;
            await db.SaveChangesAsync();
            return Results.Ok(JobDto(job));
        });

        app.MapPost("/api/_admin/jobs/{key}/run", async (AppDbContext db, string key) =>
        {
            var job = await db.JobConfigs.FirstOrDefaultAsync(j => j.Key == key);
            var def = Jobs.Find(key);
            if (job is null || def is null) return Results.NotFound(new { errors = new[] { "Unknown job." } });
            var now = DateTime.UtcNow;
            job.LastRunAt = now;
            job.NextRunAt = Jobs.NextRun(job.Schedule, now) ?? now.AddHours(1);
            // The ambient Serilog logger, not a bound parameter: a minimal API lambda treats a Serilog.ILogger parameter as a JSON body value and 500s on every request that carries one.
            var log = Serilog.Log.Logger;
            try
            {
                job.LastResult = await def.Run(db, log, CancellationToken.None);
            }
            catch (Exception ex)
            {
                job.LastResult = $"Failed: {ex.Message}";
                log.Error(ex, "Job {Key} failed on manual run", key);
            }
            await db.SaveChangesAsync();
            return Results.Ok(JobDto(job));
        });

        // Stored backups: consistent SQLite snapshots on a rolling window.
        app.MapGet("/api/_admin/backups", (AppDbContext db) =>
            Results.Ok(new { backups = BackupStore.List(BackupStore.Dir(db)) }));

        app.MapPost("/api/_admin/backups", async (AppDbContext db) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            string created;
            // A full disk is the expected failure here, and it says what is wrong rather than arriving as a 500.
            try { created = await BackupStore.CreateAsync(BackupStore.Dir(db), db, settings.BackupRetention); }
            catch (IOException ex) { return Results.BadRequest(new { errors = new[] { ex.Message } }); }
            return Results.Ok(new { created, backups = BackupStore.List(BackupStore.Dir(db)) });
        });

        app.MapGet("/api/_admin/backups/{name}", (AppDbContext db, string name) =>
        {
            var path = BackupStore.Resolve(BackupStore.Dir(db), name);
            return path is null
                ? Results.NotFound(new { errors = new[] { "No such backup." } })
                : Results.File(path, "application/vnd.sqlite3", name);
        });

        app.MapDelete("/api/_admin/backups/{name}", (AppDbContext db, string name) =>
        {
            var deleted = BackupStore.Delete(BackupStore.Dir(db), name);
            return deleted
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { errors = new[] { "No such backup." } });
        });

        app.MapGet("/api/_admin/settings", async (AppDbContext db) =>
        {
            var s = await db.SettingsAsync() ?? new AppSettings();
            var dbPath = db.Database.GetDbConnection().DataSource;
            var dbSizeBytes = ApiDtos.DatabaseBytes(db);
            // What a backup has to fit into, next to what it would cost.
            var freeDiskBytes = BackupStore.FreeBytes(BackupStore.Dir(db));

            // Same estimate the tables list sorts "Index size" by, summed instance-wide.
            var indexTables = await db.Tables.Include(t => t.Fields).ToListAsync();
            var indexRecordCounts = await db.Records.GroupBy(r => r.TableId)
                .Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            var estimatedIndexBytes = ApiDtos.EstimatedIndexBytes(indexTables,
                id => indexRecordCounts.FirstOrDefault(r => r.TableId == id)?.Count ?? 0);

            return Results.Ok(new
            {
                s.AppName,
                s.SiteUrl,
                s.LogRetentionSec,
                s.Currency,
                s.TimeZone,
                s.BackupRetention,
                s.ApiTitle,
                s.ApiDescription,
                s.AllowedOrigins,
                s.OpenApiEnabled,
                s.PublicAuthEnabled,
                s.PublicRegistrationEnabled,
                s.AnonymousAuthEnabled,
                s.AnonymousRetentionDays,
                s.AuthIssuer,
                s.AuthTokenLifetimeSec,
                s.AuthRefreshLifetimeDays,
                authJwksPath = "/api/auth/v1/jwks.json",
                authUiPath = "/auth/login",
                s.ProxyPrivateTargetsEnabled,
                s.PostgresEnabled,
                s.PostgresPort,
                s.PostgresBindAddress,
                s.TdsEnabled,
                s.TdsPort,
                s.TdsBindAddress,
                version = "0.1.0", // to-do: hardcoded for now, but should be the actual version of the running app on first minor release
                uptime = DateTime.UtcNow - Ids.StartedAt,
                openapiPath = "/api/openapi.json",
                docsPath = "/docs",
                dbPath,
                dbSizeBytes,
                freeDiskBytes,
                estimatedIndexBytes,
                tables = await db.Tables.CountAsync(),
                fields = await db.Fields.CountAsync(),
                forms = await db.FormConfigs.CountAsync(),
                records = await db.Records.CountAsync(),
                apiEnabledTables = await db.Tables.CountAsync(t => t.ApiEnabled),
                usersEnabled = await db.UserAccounts.CountAsync(u => !u.IsDisabled)
            });
        });

        app.MapPut("/api/_admin/settings", async (AppDbContext db, JsonObject body) =>
        {
            var s = await db.SettingsAsync();
            if (s == null) { s = new AppSettings(); db.AppSettings.Add(s); }
            if (body["appName"] is JsonValue nv && nv.TryGetValue<string>(out var name))
                s.AppName = string.IsNullOrWhiteSpace(name) ? "Baseport" : name.Trim();
            if (body["siteUrl"] is JsonValue uv && uv.TryGetValue<string>(out var url))
                s.SiteUrl = url.Trim();
            if (body["logRetentionSec"] is JsonValue lv && lv.TryGetValue<int>(out var retention))
                s.LogRetentionSec = retention;
            if (body["backupRetention"] is JsonValue bv && bv.TryGetValue<int>(out var backupRetention))
            {
                if (backupRetention < 1 || backupRetention > 50)
                    return Results.BadRequest(new { errors = new[] { "Backup retention must be between 1 and 50 backups." } });
                s.BackupRetention = backupRetention;
            }
            if (body["allowedOrigins"] is JsonValue ov && ov.TryGetValue<string>(out var origins))
            {
                // Stored normalised, so what an author typed and what a browser sends are compared as the same thing.
                var parsed = AllowedOrigins.Parse(origins);
                s.AllowedOrigins = AllowedOrigins.Serialize(parsed);
                EmbedOrigins.Set(s.AllowedOrigins);
            }

            if (body["currency"] is JsonValue cv && cv.TryGetValue<string>(out var currency))
            {
                var code = (currency ?? "").Trim().ToUpperInvariant();
                if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
                    return Results.BadRequest(new { errors = new[] { "Currency must be a three-letter ISO 4217 code, for example EUR." } });
                s.Currency = code;
            }

            // Storage stays UTC; this is the zone clients render in, so it only has to be one an IANA-speaking client would accept.
            if (body["timeZone"] is JsonValue tzv && tzv.TryGetValue<string>(out var timeZone))
            {
                var zone = (timeZone ?? "").Trim();
                if (zone.Length == 0) zone = "UTC";
                if (!TimeZones.IsValid(zone))
                    return Results.BadRequest(new { errors = new[] { "Time zone must be an IANA zone name, for example Europe/Amsterdam." } });
                s.TimeZone = zone;
            }
            // What the API reference says about itself.
            if (body["apiTitle"] is JsonValue tv && tv.TryGetValue<string>(out var title))
            {
                var trimmed = (title ?? "").Trim();
                if (trimmed.Length > 120)
                    return Results.BadRequest(new { errors = new[] { "The API title is too long (max 120 characters)." } });
                s.ApiTitle = trimmed.Length == 0 ? new AppSettings().ApiTitle : trimmed;
            }
            if (body["apiDescription"] is JsonValue adv && adv.TryGetValue<string>(out var apiDescription))
            {
                var text = apiDescription ?? "";
                if (text.Length > 8000)
                    return Results.BadRequest(new { errors = new[] { "The API description is too long (max 8000 characters)." } });
                s.ApiDescription = text;
            }
            if (body["openApiEnabled"] is JsonValue oav && oav.TryGetValue<bool>(out var openApiEnabled))
                s.OpenApiEnabled = openApiEnabled;

            if (body["publicAuthEnabled"] is JsonValue pav && pav.TryGetValue<bool>(out var publicAuthEnabled))
                s.PublicAuthEnabled = publicAuthEnabled;
            if (body["publicRegistrationEnabled"] is JsonValue prv && prv.TryGetValue<bool>(out var registrationEnabled))
                s.PublicRegistrationEnabled = registrationEnabled;
            if (body["anonymousAuthEnabled"] is JsonValue aav && aav.TryGetValue<bool>(out var anonymousEnabled))
                s.AnonymousAuthEnabled = anonymousEnabled;
            if (body["anonymousRetentionDays"] is JsonValue ard && ard.TryGetValue<int>(out var anonymousRetention))
            {
                if (anonymousRetention < 0 || anonymousRetention > 3650)
                    return Results.BadRequest(new { errors = new[] { "Anonymous retention must be between 0 and 3650 days." } });
                s.AnonymousRetentionDays = anonymousRetention;
            }
            if (body["authIssuer"] is JsonValue aiv && aiv.TryGetValue<string>(out var issuer))
            {
                var trimmed = (issuer ?? "").Trim();
                if (UserTokens.IssuerProblem(trimmed) is { Length: > 0 } issuerProblem)
                    return Results.BadRequest(new { errors = new[] { issuerProblem } });
                s.AuthIssuer = trimmed;
            }
            if (body["authTokenLifetimeSec"] is JsonValue atv && atv.TryGetValue<int>(out var tokenLifetime))
            {
                if (tokenLifetime < UserTokens.MinTokenLifetimeSec || tokenLifetime > UserTokens.MaxTokenLifetimeSec)
                    return Results.BadRequest(new { errors = new[] { $"The token lifetime must be between {UserTokens.MinTokenLifetimeSec} and {UserTokens.MaxTokenLifetimeSec} seconds." } });
                s.AuthTokenLifetimeSec = tokenLifetime;
            }
            if (body["authRefreshLifetimeDays"] is JsonValue arv && arv.TryGetValue<int>(out var refreshLifetime))
            {
                if (refreshLifetime < UserTokens.MinRefreshLifetimeDays || refreshLifetime > UserTokens.MaxRefreshLifetimeDays)
                    return Results.BadRequest(new { errors = new[] { $"The refresh lifetime must be between {UserTokens.MinRefreshLifetimeDays} and {UserTokens.MaxRefreshLifetimeDays} days." } });
                s.AuthRefreshLifetimeDays = refreshLifetime;
            }

            var providerError = ApplyProviderSettings(body, s);
            if (providerError is not null) return Results.BadRequest(new { errors = new[] { providerError } });

            if (body["proxyPrivateTargetsEnabled"] is JsonValue papt && papt.TryGetValue<bool>(out var allowPrivate))
                s.ProxyPrivateTargetsEnabled = allowPrivate;

            await db.SaveChangesAsync();
            UserTokens.Configure(s);
            ProxyTarget.Configure(s);
            return Results.Ok(new
            {
                s.AppName, s.SiteUrl, s.LogRetentionSec, s.Currency, s.TimeZone, s.BackupRetention, s.ApiTitle, s.ApiDescription, s.AllowedOrigins, s.OpenApiEnabled,
                s.PublicAuthEnabled, s.PublicRegistrationEnabled, s.AnonymousAuthEnabled, s.AnonymousRetentionDays,
                s.AuthIssuer, s.AuthTokenLifetimeSec, s.AuthRefreshLifetimeDays,
                s.ProxyPrivateTargetsEnabled,
                s.PostgresEnabled, s.PostgresPort, s.PostgresBindAddress, s.TdsEnabled, s.TdsPort, s.TdsBindAddress
            });
        });

        app.MapPost("/api/_admin/settings/auth-key", async (AppDbContext db) =>
        {
            var s = await db.SettingsAsync();
            if (s == null) { s = new AppSettings(); db.AppSettings.Add(s); }

            KeyStore.Write(db, UserTokens.Rotate());
            await db.UserSessions.ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            return Results.Ok(new { rotated = true });
        });

        // Read-only SQL console against the SQLite store.
        app.MapPost("/api/_admin/sql", async (AppDbContext db, JsonObject body) =>
        {
            var sql = body["sql"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim() : "";
            var validationError = SqlEngine.Validate(sql);
            if (validationError != null)
                return Results.BadRequest(new { errors = new[] { validationError } });
            var run = await SqlEngine.ReadAsync(db, sql, WireCatalog.Views, restrict: false);
            return run.Error is not null
                ? Results.BadRequest(new { errors = new[] { run.Error } })
                : Results.Ok(new { columns = run.Columns, rows = run.Rows, truncated = run.Truncated, rowCount = run.Rows.Count });
        });

        // Saved queries: CRUD + execute.
        app.MapGet("/api/_admin/queries", async (AppDbContext db) =>
            Results.Ok((await db.SavedQueries.OrderBy(q => q.Name).ToListAsync()).Select(QueryDto)));

        app.MapPost("/api/_admin/queries", async (AppDbContext db, JsonObject body) =>
        {
            var name = body["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n.Trim() : "";
            var sql = body["sql"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim() : "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { errors = new[] { "Query name is required." } });
            var validationError = SqlEngine.Validate(sql);
            if (validationError != null)
                return Results.BadRequest(new { errors = new[] { validationError } });
            var now = DateTime.UtcNow;
            var query = new SavedQuery { Id = Ids.NewShortId(12), Name = name, Sql = sql, CreatedAt = now, UpdatedAt = now };
            if (ApplySchedule(body, query, now) is { } scheduleError)
                return Results.BadRequest(new { errors = new[] { scheduleError } });
            db.SavedQueries.Add(query);
            await db.SaveChangesAsync();
            return Results.Ok(QueryDto(query));
        });

        app.MapPatch("/api/_admin/queries/{pid}", async (AppDbContext db, string pid, JsonObject body) =>
        {
            var query = await db.SavedQueries.FirstOrDefaultAsync(q => q.Id == pid);
            if (query == null) return Results.NotFound();
            if (body["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && !string.IsNullOrWhiteSpace(n.Trim()))
                query.Name = n.Trim();
            if (body["sql"] is JsonValue sv && sv.TryGetValue<string>(out var s))
            {
                var validationError = SqlEngine.Validate(s.Trim());
                if (validationError != null)
                    return Results.BadRequest(new { errors = new[] { validationError } });
                query.Sql = s.Trim();
            }
            var edited = DateTime.UtcNow;
            if (ApplySchedule(body, query, edited) is { } scheduleError)
                return Results.BadRequest(new { errors = new[] { scheduleError } });
            query.UpdatedAt = edited;
            await db.SaveChangesAsync();
            return Results.Ok(QueryDto(query));
        });

        app.MapDelete("/api/_admin/queries/{pid}", async (AppDbContext db, string pid) =>
        {
            var query = await db.SavedQueries.FirstOrDefaultAsync(q => q.Id == pid);
            if (query == null) return Results.NotFound();
            db.SavedQueries.Remove(query);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Execute a saved query and record when it last ran.
        app.MapPost("/api/_admin/queries/{pid}/execute", async (AppDbContext db, string pid) =>
        {
            var query = await db.SavedQueries.FirstOrDefaultAsync(q => q.Id == pid);
            if (query == null) return Results.NotFound();
            var validationError = SqlEngine.Validate(query.Sql);
            if (validationError != null)
                return Results.BadRequest(new { errors = new[] { validationError } });
            var run = await SqlEngine.ReadAsync(db, query.Sql, WireCatalog.Views, restrict: false);
            if (run.Error is not null) return Results.BadRequest(new { errors = new[] { run.Error } });

            query.LastExecutedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { columns = run.Columns, rows = run.Rows, truncated = run.Truncated, rowCount = run.Rows.Count });
        });

        // Runs a scheduled query the way the tick would, webhook included, so an operator can prove the destination works without waiting for the cron.
        app.MapPost("/api/_admin/queries/{pid}/run", async (AppDbContext db, IHttpClientFactory http, string pid) =>
        {
            var query = await db.SavedQueries.FirstOrDefaultAsync(q => q.Id == pid);
            if (query == null) return Results.NotFound();
            if (query.Schedule.Length == 0)
                return Results.BadRequest(new { errors = new[] { "This query has no schedule. Give it a cron expression first." } });

            await ScheduledQueries.RunAsync(db, query, http, DateTime.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
            return Results.Ok(QueryDto(query));
        });

    }

    private static object QueryDto(SavedQuery q) => new
    {
        q.Id, q.Name, q.Sql, q.CreatedAt, q.UpdatedAt, q.LastExecutedAt,
        q.Schedule, q.ScheduleEnabled, q.WebhookUrl, q.NextRunAt, q.LastResult
    };

    // Returns the message to hand back, or null. The cron and the destination are checked when the query is saved rather than when the tick reaches it, the same way an access rule is: a typo is a message in the sheet, not a failure nobody sees until tomorrow morning.
    private static string? ApplySchedule(JsonObject body, SavedQuery query, DateTime now)
    {
        if (body["schedule"] is JsonValue cv && cv.TryGetValue<string>(out var cron))
        {
            var trimmed = (cron ?? "").Trim();
            if (ScheduledQueries.ScheduleProblem(trimmed) is { } problem) return problem;
            query.Schedule = trimmed;
        }
        if (body["webhookUrl"] is JsonValue wv && wv.TryGetValue<string>(out var url))
        {
            var trimmed = (url ?? "").Trim();
            if (ScheduledQueries.WebhookProblem(trimmed) is { } problem) return problem;
            query.WebhookUrl = trimmed;
        }
        if (body["scheduleEnabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled))
            query.ScheduleEnabled = enabled;

        if (query.Schedule.Length == 0) query.ScheduleEnabled = false;
        query.NextRunAt = query.ScheduleEnabled ? Jobs.NextRun(query.Schedule, now) : null;
        return null;
    }

    // shared by the settings put endpoint: keeps postgres/tds bind-address and port validation in one place
    private static string? ApplyProviderSettings(JsonObject body, AppSettings s)
    {
        if (body["postgresEnabled"] is JsonValue pev && pev.TryGetValue<bool>(out var postgresEnabled))
            s.PostgresEnabled = postgresEnabled;
        if (body["postgresPort"] is JsonValue ppv && ppv.TryGetValue<int>(out var postgresPort))
        {
            if (postgresPort is < 1 or > 65535) return "Postgres port must be between 1 and 65535.";
            s.PostgresPort = postgresPort;
        }
        if (body["postgresBindAddress"] is JsonValue pbv && pbv.TryGetValue<string>(out var postgresBind))
        {
            if (!IPAddress.TryParse(postgresBind, out _)) return "Postgres bind address must be a valid IP address.";
            s.PostgresBindAddress = postgresBind;
        }

        if (body["tdsEnabled"] is JsonValue tev && tev.TryGetValue<bool>(out var tdsEnabled))
            s.TdsEnabled = tdsEnabled;
        if (body["tdsPort"] is JsonValue tpv && tpv.TryGetValue<int>(out var tdsPort))
        {
            if (tdsPort is < 1 or > 65535) return "TDS port must be between 1 and 65535.";
            s.TdsPort = tdsPort;
        }
        if (body["tdsBindAddress"] is JsonValue tbv && tbv.TryGetValue<string>(out var tdsBind))
        {
            if (!IPAddress.TryParse(tdsBind, out _)) return "TDS bind address must be a valid IP address.";
            s.TdsBindAddress = tdsBind;
        }

        return null;
    }

    // AccountValidation.Validate returns flat messages; the ones it writes always name the field they're about
    private static List<string> InvalidAccountFields(List<string> errors)
    {
        var invalid = new List<string>();
        if (errors.Any(e => e.StartsWith("Username", StringComparison.Ordinal))) invalid.Add("username");
        if (errors.Any(e => e.StartsWith("Email", StringComparison.Ordinal))) invalid.Add("email");
        return invalid;
    }

    // An admin account is changed, deleted and demoted only with shell access, so console access alone cannot take another operator over. TrailBase draws the same line (crates/core/src/admin/user/update_user.rs:38).
    public const string AdminOnlyByCli = "An admin's password, role and disabled state are set with the CLI, and an admin cannot be deleted here: baseport accounts --help. The name and the address are editable.";

    // Nobody reaches the console once the last admin who can sign in is gone, and there is no way back in.
    public static async Task<bool> IsLastEnabledAdmin(AppDbContext db, UserAccount account) =>
        account.Role == AccountRoles.Admin && !account.IsDisabled &&
        await db.UserAccounts.CountAsync(a => a.Role == AccountRoles.Admin && !a.IsDisabled && a.Id != account.Id) == 0;
}
