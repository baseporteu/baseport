using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Baseport;

public enum WireDialect { Postgres, Tds }

// Records live as JSON in one shared _records table, so a sql client browsing this instance would otherwise see the storage schema and nothing an author actually built.
// This projects the author's tables back out: a temp view per table turns its fields into real columns, and a snapshot catalog describes those views in the shape the client's dialect expects.
//
// ponytail: rebuilt per connection, and SqlEngine opens one connection per query, so a browse pays two metadata reads per statement. Cache per wire session if that ever shows up in a profile.
public static class WireCatalog
{
    private sealed record CatalogColumn(string Name, string DataType, bool Required);
    private sealed record CatalogTable(string Id, string Name, int Oid, long Rows, List<CatalogColumn> Columns, string ReadRule);

    // userId is the account the wire session authenticated as. It scopes the row views to the same rows the REST api would return that account (pillar 15), so the wire is not a way around a table's read rule.
    public static void Apply(SqliteConnection conn, WireDialect dialect, string? userId)
    {
        var tables = Read(conn, publishedOnly: true);
        CreateRowViews(conn, tables, userId, readRules: true);

        if (dialect == WireDialect.Postgres) BuildPostgres(conn, tables);
        else BuildTds(conn, tables);
    }

    // The same row views for the admin sql console: every table rather than only the published ones, no read rule, and no dialect catalog, because the console already runs on an unrestricted handle that can read main._records directly.
    public static void Views(SqliteConnection conn) => CreateRowViews(conn, Read(conn, publishedOnly: false), null, readRules: false);

    // A wire client authenticates with an api token but must only ever see the projected author tables, never the storage schema behind them: _users holds password hashes and _settings holds the jwt signing key, so a raw SELECT there is a full-instance compromise. The temp views and the emulated pg_catalog/information_schema are the whole intended surface. This denies every direct read of the main schema (the system tables and the raw _records store) while leaving those views readable; a view's own read of main._records reports the view as the innermost object, so projected author data still resolves. Installed only on the untrusted wire connection, and cleared before that connection returns to the pool.
    private static readonly delegate_authorizer DenyMainReads = (_, action, _, _, dbName, viaObject) =>
        action == raw.SQLITE_READ && dbName.utf8_to_string() == "main" && string.IsNullOrEmpty(viaObject.utf8_to_string())
            ? raw.SQLITE_DENY
            : raw.SQLITE_OK;

    public static void Restrict(SqliteConnection conn) => raw.sqlite3_set_authorizer(conn.Handle, DenyMainReads, null);

    public static void Unrestrict(SqliteConnection conn) => raw.sqlite3_set_authorizer(conn.Handle, (delegate_authorizer?)null, null);

    private static List<CatalogTable> Read(SqliteConnection conn, bool publishedOnly)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var reader = Query(conn, "SELECT TableId, COUNT(*) FROM _records GROUP BY TableId"))
            while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt64(1);

        var columns = new Dictionary<string, List<CatalogColumn>>(StringComparer.Ordinal);
        using (var reader = Query(conn, "SELECT TableId, Name, DataType, IsRequired FROM _fields ORDER BY TableId, Position, Id"))
            while (reader.Read())
            {
                var name = reader.GetString(1);
                // a name outside this set would have to be escaped into both a json path and an identifier; the authoring side already refuses them, so anything else is skipped rather than trusted
                if (!IsPlainIdentifier(name)) continue;
                if (!columns.TryGetValue(reader.GetString(0), out var list)) columns[reader.GetString(0)] = list = new List<CatalogColumn>();
                list.Add(new CatalogColumn(name, reader.IsDBNull(2) ? "text" : reader.GetString(2), !reader.IsDBNull(3) && reader.GetInt64(3) != 0));
            }

        var tables = new List<CatalogTable>();
        var oid = 16384;
        // The wire exposes only published tables (ApiEnabled), the same set a token reaches over REST, never an author's unpublished working table. The console, already inside the operator's own handle, sees them all.
        using (var reader = Query(conn, $"SELECT Id, Name, ReadRule FROM _tables WHERE IsProxy = 0{(publishedOnly ? " AND ApiEnabled = 1" : "")} ORDER BY Name"))
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                // a leading underscore is the storage schema's own prefix, and a temp view takes precedence over main, so projecting one would shadow _records or _users out from under the console
                if (!IsPlainIdentifier(name) || name[0] == '_') continue;
                var readRule = reader.IsDBNull(2) ? "" : reader.GetString(2);
                tables.Add(new CatalogTable(id, name, oid, counts.GetValueOrDefault(id), columns.GetValueOrDefault(id) ?? new List<CatalogColumn>(), readRule));
                oid += 16;
            }
        return tables;
    }

    // The view is what makes the catalog honest: every object it lists can actually be selected from.
    private static void CreateRowViews(SqliteConnection conn, List<CatalogTable> tables, string? userId, bool readRules)
    {
        foreach (var table in tables)
        {
            var projection = new StringBuilder($"SELECT r.Id AS {Quote("id")}, r.CreatedAt AS {Quote("created_at")}, r.UpdatedAt AS {Quote("updated_at")}");
            foreach (var column in table.Columns)
                projection.Append($", json_extract(r.JsonData, '$.{column.Name}') AS {Quote(column.Name)}");
            projection.Append($" FROM main._records r WHERE r.TableId = {Literal(table.Id)}");

            // A rule naming a field this catalog skipped rewrites to NULL, which reads as a refusal, so an unprojectable field closes the view rather than opening it.
            var fields = table.Columns.Select(c => new FieldDefinition { Name = c.Name }).ToList();
            if (readRules && RecordAccess.ReadClauseLiteral(table.ReadRule, fields, "r", userId) is { } clause)
                projection.Append($" AND COALESCE(({clause}), 0)");

            Exec(conn, $"DROP VIEW IF EXISTS temp.{Quote(table.Name)}");
            Exec(conn, $"CREATE TEMP VIEW {Quote(table.Name)} AS {projection}");
        }
    }

    private static void BuildPostgres(SqliteConnection conn, List<CatalogTable> tables)
    {
        Attach(conn, "pg_catalog");
        Attach(conn, "information_schema");

        Fill(conn, "pg_catalog", "pg_namespace", "oid, nspname, nspowner, nspacl",
        [
            "2200, 'public', 10, NULL",
            "11, 'pg_catalog', 10, NULL",
            "13000, 'information_schema', 10, NULL",
        ]);

        Fill(conn, "pg_catalog", "pg_class",
            "oid, relname, relnamespace, reltype, reloftype, relowner, relam, relfilenode, reltablespace, relpages, reltuples, relallvisible, reltoastrelid, relhasindex, relisshared, relpersistence, relkind, relnatts, relchecks, relhasrules, relhastriggers, relhassubclass, relrowsecurity, relforcerowsecurity, relispopulated, relreplident, relispartition, relacl, reloptions, relpartbound",
            tables.Select(t =>
                $"{t.Oid}, {Literal(t.Name)}, 2200, 0, 0, 10, 0, {t.Oid}, 0, 1, {t.Rows}, 0, 0, 0, 0, 'p', 'r', {t.Columns.Count + SystemColumns.Length}, 0, 0, 0, 0, 0, 0, 1, 'd', 0, NULL, NULL, NULL"));

        Fill(conn, "pg_catalog", "pg_attribute",
            "attrelid, attname, atttypid, attnum, attnotnull, atttypmod, attisdropped, attlen, attndims, atthasdef, attidentity, attgenerated, attcollation, attalign, attstorage, attacl, attoptions, attfdwoptions, atthasmissing, attinhcount, attstattarget",
            tables.SelectMany(t => Attributes(t).Select((c, i) =>
                $"{t.Oid}, {Literal(c.Name)}, {PostgresTypeOid(c.DataType)}, {i + 1}, {(c.Required ? 1 : 0)}, -1, 0, -1, 0, 0, '', '', 0, 'i', 'x', NULL, NULL, NULL, 0, 0, -1")));

        Fill(conn, "pg_catalog", "pg_type",
            "oid, typname, typnamespace, typowner, typlen, typbyval, typtype, typcategory, typispreferred, typisdefined, typdelim, typrelid, typelem, typarray, typnotnull, typbasetype, typtypmod, typndims, typdefault",
            PostgresTypes.Select(t => $"{t.Oid}, {Literal(t.Name)}, 11, 10, -1, 0, 'b', {Literal(t.Category)}, 0, 1, ',', 0, 0, 0, 0, 0, -1, 0, NULL"));

        Fill(conn, "pg_catalog", "pg_tables", "schemaname, tablename, tableowner, tablespace, hasindexes, hasrules, hastriggers, rowsecurity",
            tables.Select(t => $"'public', {Literal(t.Name)}, 'baseport', NULL, 0, 0, 0, 0"));

        Fill(conn, "pg_catalog", "pg_database",
            "oid, datname, datdba, encoding, datcollate, datctype, datistemplate, datallowconn, datconnlimit, dattablespace, datacl",
            ["1, 'baseport', 10, 6, 'en_US.UTF-8', 'en_US.UTF-8', 0, 1, -1, 1663, NULL"]);

        Fill(conn, "pg_catalog", "pg_roles",
            "oid, rolname, rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolcanlogin, rolreplication, rolconnlimit, rolbypassrls",
            ["10, 'baseport', 1, 1, 1, 1, 1, 0, -1, 1"]);

        Fill(conn, "pg_catalog", "pg_settings", "name, setting, category, short_desc, vartype, source",
        [
            "'search_path', 'public', 'Client Connection Defaults', 'Sets the schema search order.', 'string', 'default'",
            "'server_version', '15.0', 'Preset Options', 'Shows the server version.', 'string', 'default'",
            "'TimeZone', 'UTC', 'Client Connection Defaults', 'Sets the time zone.', 'string', 'default'",
        ]);

        // Emulated as empty rather than left missing: a browser that joins one of these gets no rows instead of a failed statement.
        Empty(conn, "pg_catalog", "pg_views", "schemaname, viewname, viewowner, definition");
        Empty(conn, "pg_catalog", "pg_indexes", "schemaname, tablename, indexname, tablespace, indexdef");
        Empty(conn, "pg_catalog", "pg_index", "indexrelid, indrelid, indnatts, indisunique, indisprimary, indisexclusion, indimmediate, indisclustered, indisvalid, indkey");
        Empty(conn, "pg_catalog", "pg_constraint", "oid, conname, connamespace, contype, condeferrable, condeferred, convalidated, conrelid, contypid, conindid, confrelid, conkey, confkey, consrc");
        Empty(conn, "pg_catalog", "pg_description", "objoid, classoid, objsubid, description");
        Empty(conn, "pg_catalog", "pg_proc", "oid, proname, pronamespace, proowner, prorettype, proargtypes, prokind");
        Empty(conn, "pg_catalog", "pg_trigger", "oid, tgrelid, tgname, tgfoid, tgtype, tgenabled, tgisinternal");
        Empty(conn, "pg_catalog", "pg_sequence", "seqrelid, seqtypid, seqstart, seqincrement, seqmax, seqmin, seqcache, seqcycle");
        Empty(conn, "pg_catalog", "pg_inherits", "inhrelid, inhparent, inhseqno");
        Empty(conn, "pg_catalog", "pg_enum", "oid, enumtypid, enumsortorder, enumlabel");
        Empty(conn, "pg_catalog", "pg_extension", "oid, extname, extowner, extnamespace, extrelocatable, extversion");
        Empty(conn, "pg_catalog", "pg_am", "oid, amname, amhandler, amtype");
        Empty(conn, "pg_catalog", "pg_attrdef", "oid, adrelid, adnum, adbin");
        Empty(conn, "pg_catalog", "pg_collation", "oid, collname, collnamespace, collowner, collprovider, collcollate, collctype");
        Empty(conn, "pg_catalog", "pg_partitioned_table", "partrelid, partstrat, partnatts, partattrs");

        Fill(conn, "information_schema", "schemata", "catalog_name, schema_name, schema_owner, default_character_set_name",
            ["'baseport', 'public', 'baseport', 'UTF8'"]);

        Fill(conn, "information_schema", "tables",
            "table_catalog, table_schema, table_name, table_type, self_referencing_column_name, reference_generation, user_defined_type_catalog, user_defined_type_schema, user_defined_type_name, is_insertable_into, is_typed, commit_action",
            tables.Select(t => $"'baseport', 'public', {Literal(t.Name)}, 'BASE TABLE', NULL, NULL, NULL, NULL, NULL, 'NO', 'NO', NULL"));

        Fill(conn, "information_schema", "columns",
            "table_catalog, table_schema, table_name, column_name, ordinal_position, column_default, is_nullable, data_type, character_maximum_length, character_octet_length, numeric_precision, numeric_precision_radix, numeric_scale, datetime_precision, udt_catalog, udt_schema, udt_name, is_identity, is_generated, is_updatable",
            tables.SelectMany(t => Attributes(t).Select((c, i) =>
            {
                var type = PostgresTypeName(c.DataType);
                return $"'baseport', 'public', {Literal(t.Name)}, {Literal(c.Name)}, {i + 1}, NULL, {(c.Required ? "'NO'" : "'YES'")}, {Literal(type)}, NULL, NULL, NULL, NULL, NULL, NULL, 'baseport', 'pg_catalog', {Literal(type)}, 'NO', 'NEVER', 'NO'";
            })));

        Empty(conn, "information_schema", "views", "table_catalog, table_schema, table_name, view_definition, check_option, is_updatable");
        Empty(conn, "information_schema", "table_constraints", "constraint_catalog, constraint_schema, constraint_name, table_catalog, table_schema, table_name, constraint_type, is_deferrable, initially_deferred");
        Empty(conn, "information_schema", "key_column_usage", "constraint_catalog, constraint_schema, constraint_name, table_catalog, table_schema, table_name, column_name, ordinal_position");
        Empty(conn, "information_schema", "referential_constraints", "constraint_catalog, constraint_schema, constraint_name, unique_constraint_catalog, unique_constraint_schema, unique_constraint_name, match_option, update_rule, delete_rule");
        Empty(conn, "information_schema", "constraint_column_usage", "table_catalog, table_schema, table_name, column_name, constraint_catalog, constraint_schema, constraint_name");
        Empty(conn, "information_schema", "routines", "specific_catalog, specific_schema, specific_name, routine_catalog, routine_schema, routine_name, routine_type, data_type");
        Empty(conn, "information_schema", "sequences", "sequence_catalog, sequence_schema, sequence_name, data_type, start_value, minimum_value, maximum_value, increment");
    }

    private static void BuildTds(SqliteConnection conn, List<CatalogTable> tables)
    {
        Attach(conn, "sys");
        Attach(conn, "INFORMATION_SCHEMA");

        Fill(conn, "sys", "databases",
            "name, database_id, source_database_id, owner_sid, create_date, compatibility_level, collation_name, user_access, user_access_desc, state, state_desc, is_read_only, recovery_model, recovery_model_desc, is_auto_close_on, is_in_standby",
            ["'baseport', 5, NULL, NULL, '2020-01-01 00:00:00', 150, 'SQL_Latin1_General_CP1_CI_AS', 0, 'MULTI_USER', 0, 'ONLINE', 1, 3, 'SIMPLE', 0, 0"]);

        Fill(conn, "sys", "schemas", "name, schema_id, principal_id", ["'dbo', 1, 1"]);

        var objectRows = tables.Select(t =>
            $"{Literal(t.Name)}, {t.Oid}, 1, 1, 0, 'U ', 'USER_TABLE', '2020-01-01 00:00:00', '2020-01-01 00:00:00', 0, 0, 0").ToList();

        Fill(conn, "sys", "objects",
            "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, create_date, modify_date, is_ms_shipped, is_published, is_schema_published",
            objectRows);
        Fill(conn, "sys", "all_objects",
            "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, create_date, modify_date, is_ms_shipped, is_published, is_schema_published",
            objectRows);

        Fill(conn, "sys", "tables",
            "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, create_date, modify_date, is_ms_shipped, is_published, is_schema_published, lob_data_space_id, filestream_data_space_id, max_column_id_used, lock_on_bulk_load, uses_ansi_nulls, is_replicated, has_replication_filter, is_merge_published, is_sync_tran_subscribed, has_unchecked_assembly_data, text_in_row_limit, large_value_types_out_of_row, is_tracked_by_cdc, lock_escalation, lock_escalation_desc, is_filetable, is_memory_optimized, durability, durability_desc, temporal_type, temporal_type_desc, history_table_id, is_external",
            tables.Select(t =>
                $"{Literal(t.Name)}, {t.Oid}, 1, 1, 0, 'U ', 'USER_TABLE', '2020-01-01 00:00:00', '2020-01-01 00:00:00', 0, 0, 0, 0, 0, {t.Columns.Count + SystemColumns.Length}, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'TABLE', 0, 0, 0, 'SCHEMA_AND_DATA', 0, 'NON_TEMPORAL_TABLE', NULL, 0"));

        Fill(conn, "sys", "columns",
            "object_id, name, column_id, system_type_id, user_type_id, max_length, precision, scale, collation_name, is_nullable, is_ansi_padded, is_rowguidcol, is_identity, is_computed, is_filestream, is_replicated, is_non_sql_subscribed, is_merge_published, is_dts_replicated, is_xml_document, xml_collection_id, default_object_id, rule_object_id, is_sparse, is_column_set, generated_always_type, generated_always_type_desc, is_hidden, is_masked",
            tables.SelectMany(t => Attributes(t).Select((c, i) =>
            {
                var type = TdsType(c.DataType);
                return $"{t.Oid}, {Literal(c.Name)}, {i + 1}, {type.SystemTypeId}, {type.SystemTypeId}, {type.MaxLength}, {type.Precision}, {type.Scale}, 'SQL_Latin1_General_CP1_CI_AS', {(c.Required ? 0 : 1)}, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'NOT_APPLICABLE', 0, 0";
            })));

        Fill(conn, "sys", "types",
            "name, system_type_id, user_type_id, schema_id, principal_id, max_length, precision, scale, collation_name, is_nullable, is_user_defined, is_assembly_type, default_object_id, rule_object_id, is_table_type",
            TdsTypes.Select(t => $"{Literal(t.Name)}, {t.SystemTypeId}, {t.SystemTypeId}, 4, NULL, {t.MaxLength}, {t.Precision}, {t.Scale}, NULL, 1, 0, 0, 0, 0, 0"));

        Empty(conn, "sys", "indexes", "object_id, name, index_id, type, type_desc, is_unique, data_space_id, ignore_dup_key, is_primary_key, is_unique_constraint, fill_factor, is_padded, is_disabled, is_hypothetical, allow_row_locks, allow_page_locks, has_filter, filter_definition");
        Empty(conn, "sys", "index_columns", "object_id, index_id, index_column_id, column_id, key_ordinal, partition_ordinal, is_descending_key, is_included_column");
        Empty(conn, "sys", "foreign_keys", "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, referenced_object_id, key_index_id, is_disabled, is_not_trusted, delete_referential_action, update_referential_action");
        Empty(conn, "sys", "foreign_key_columns", "constraint_object_id, constraint_column_id, parent_object_id, parent_column_id, referenced_object_id, referenced_column_id");
        Empty(conn, "sys", "key_constraints", "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, unique_index_id, is_system_named");
        Empty(conn, "sys", "check_constraints", "name, object_id, schema_id, parent_object_id, type, type_desc, definition, is_disabled");
        Empty(conn, "sys", "default_constraints", "name, object_id, schema_id, parent_object_id, type, type_desc, parent_column_id, definition");
        Empty(conn, "sys", "extended_properties", "class, class_desc, major_id, minor_id, name, value");
        Empty(conn, "sys", "sql_modules", "object_id, definition, uses_ansi_nulls, uses_quoted_identifier, is_schema_bound");
        Empty(conn, "sys", "parameters", "object_id, name, parameter_id, system_type_id, user_type_id, max_length, precision, scale, is_output");
        Empty(conn, "sys", "triggers", "name, object_id, parent_class, parent_class_desc, parent_id, type, type_desc, is_disabled, is_ms_shipped, is_instead_of_trigger");
        Empty(conn, "sys", "synonyms", "name, object_id, principal_id, schema_id, type, type_desc, base_object_name");
        Empty(conn, "sys", "views", "name, object_id, principal_id, schema_id, parent_object_id, type, type_desc, is_ms_shipped, with_check_option, is_date_correlation_view");
        Empty(conn, "sys", "database_principals", "name, principal_id, type, type_desc, default_schema_name, create_date, modify_date, is_fixed_role");
        Empty(conn, "sys", "server_principals", "name, principal_id, sid, type, type_desc, is_disabled");
        Empty(conn, "sys", "servers", "server_id, name, product, provider, data_source, is_linked");
        Empty(conn, "sys", "partitions", "partition_id, object_id, index_id, partition_number, rows, data_compression, data_compression_desc");
        Empty(conn, "sys", "allocation_units", "allocation_unit_id, type, type_desc, container_id, total_pages, used_pages, data_pages");
        Empty(conn, "sys", "data_spaces", "name, data_space_id, type, type_desc, is_default, is_system");
        Empty(conn, "sys", "filegroups", "name, data_space_id, type, type_desc, is_default, is_system, filegroup_id");
        Empty(conn, "sys", "dm_exec_sessions", "session_id, login_time, host_name, program_name, login_name, status, database_id");

        Fill(conn, "INFORMATION_SCHEMA", "SCHEMATA", "CATALOG_NAME, SCHEMA_NAME, SCHEMA_OWNER, DEFAULT_CHARACTER_SET_NAME",
            ["'baseport', 'dbo', 'dbo', 'UNICODE'"]);

        Fill(conn, "INFORMATION_SCHEMA", "TABLES", "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE",
            tables.Select(t => $"'baseport', 'dbo', {Literal(t.Name)}, 'BASE TABLE'"));

        Fill(conn, "INFORMATION_SCHEMA", "COLUMNS",
            "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_DEFAULT, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION, COLLATION_NAME",
            tables.SelectMany(t => Attributes(t).Select((c, i) =>
            {
                var type = TdsType(c.DataType);
                return $"'baseport', 'dbo', {Literal(t.Name)}, {Literal(c.Name)}, {i + 1}, NULL, {(c.Required ? "'NO'" : "'YES'")}, {Literal(type.Name)}, {(type.MaxLength < 0 ? "NULL" : type.MaxLength.ToString())}, {type.Precision}, {type.Scale}, NULL, 'SQL_Latin1_General_CP1_CI_AS'";
            })));

        Empty(conn, "INFORMATION_SCHEMA", "VIEWS", "TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, VIEW_DEFINITION, CHECK_OPTION, IS_UPDATABLE");
        Empty(conn, "INFORMATION_SCHEMA", "TABLE_CONSTRAINTS", "CONSTRAINT_CATALOG, CONSTRAINT_SCHEMA, CONSTRAINT_NAME, TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_TYPE, IS_DEFERRABLE, INITIALLY_DEFERRED");
        Empty(conn, "INFORMATION_SCHEMA", "KEY_COLUMN_USAGE", "CONSTRAINT_CATALOG, CONSTRAINT_SCHEMA, CONSTRAINT_NAME, TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION");
        Empty(conn, "INFORMATION_SCHEMA", "ROUTINES", "SPECIFIC_CATALOG, SPECIFIC_SCHEMA, SPECIFIC_NAME, ROUTINE_CATALOG, ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE, DATA_TYPE");
    }

    // id, created_at and updated_at are real columns of the view, so they belong in the catalog beside the author's fields.
    private static IEnumerable<CatalogColumn> Attributes(CatalogTable table) =>
        SystemColumns.Concat(table.Columns);

    private static readonly CatalogColumn[] SystemColumns =
    [
        new("id", "text", true), new("created_at", "datetime", true), new("updated_at", "datetime", true)
    ];

    private static readonly (string Name, int Oid, string Category)[] PostgresTypes =
    [
        ("bool", 16, "B"), ("int8", 20, "N"), ("numeric", 1700, "N"), ("text", 25, "S"),
        ("varchar", 1043, "S"), ("date", 1082, "D"), ("time", 1083, "D"), ("timestamp", 1114, "D"), ("json", 114, "U"),
    ];

    private static string PostgresTypeName(string dataType) => dataType.ToLowerInvariant() switch
    {
        "boolean" => "boolean",
        "number" or "rating" => "numeric",
        "currency" => "numeric",
        "date" => "date",
        "time" => "time",
        "datetime" => "timestamp without time zone",
        "json" or "array" or "multiselect" => "json",
        _ => "text",
    };

    private static int PostgresTypeOid(string dataType) => dataType.ToLowerInvariant() switch
    {
        "boolean" => 16,
        "number" or "rating" or "currency" => 1700,
        "date" => 1082,
        "time" => 1083,
        "datetime" => 1114,
        "json" or "array" or "multiselect" => 114,
        _ => 25,
    };

    private static readonly (string Name, int SystemTypeId, int MaxLength, int Precision, int Scale)[] TdsTypes =
    [
        ("nvarchar", 231, 8000, 0, 0), ("bit", 104, 1, 1, 0), ("decimal", 106, 17, 18, 6),
        ("date", 40, 3, 10, 0), ("time", 41, 5, 16, 7), ("datetime2", 42, 8, 27, 7),
    ];

    private static (string Name, int SystemTypeId, int MaxLength, int Precision, int Scale) TdsType(string dataType) => dataType.ToLowerInvariant() switch
    {
        "boolean" => ("bit", 104, 1, 1, 0),
        "number" or "rating" or "currency" => ("decimal", 106, 17, 18, 6),
        "date" => ("date", 40, 3, 10, 0),
        "time" => ("time", 41, 5, 16, 7),
        "datetime" => ("datetime2", 42, 8, 27, 7),
        "longtext" or "richtext" or "json" or "array" or "multiselect" => ("nvarchar", 231, -1, 0, 0),
        _ => ("nvarchar", 231, 8000, 0, 0),
    };

    private static void Attach(SqliteConnection conn, string schema)
    {
        // sqlite refuses a second attach under the same name, and the in-memory store reuses one connection for the life of the process
        using var reader = Query(conn, "PRAGMA database_list");
        while (reader.Read())
            if (string.Equals(reader.GetString(1), schema, StringComparison.OrdinalIgnoreCase)) return;

        Exec(conn, $"ATTACH ':memory:' AS {Quote(schema)}");
    }

    private static void Fill(SqliteConnection conn, string schema, string name, string columns, IEnumerable<string> rows)
    {
        Exec(conn, $"DROP TABLE IF EXISTS {Quote(schema)}.{Quote(name)}");
        Exec(conn, $"CREATE TABLE {Quote(schema)}.{Quote(name)} ({string.Join(", ", columns.Split(',').Select(c => $"{Quote(c.Trim())} "))})");

        var values = rows.ToList();
        if (values.Count == 0) return;
        Exec(conn, $"INSERT INTO {Quote(schema)}.{Quote(name)} VALUES {string.Join(", ", values.Select(v => $"({v})"))}");
    }

    private static void Empty(SqliteConnection conn, string schema, string name, string columns) =>
        Fill(conn, schema, name, columns, []);

    private static SqliteDataReader Query(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteReader();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool IsPlainIdentifier(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
