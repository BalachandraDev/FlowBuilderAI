using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Persistence;

/// <summary>Where the app found its database when it started, and why, so the administrator can see it.</summary>
public sealed record DatabaseStartupState(bool UsingSavedServer, string? Problem);

/// <summary>
/// The database the administrator moved the app to is saved in a small file next to the app (App_Data/database.override), protected with
/// the same key as the saved AI keys, because the database cannot hold the address of itself. At start-up the file is read first.
/// </summary>
public static class DatabaseOverride
{
    public static string PathFor(IConfiguration config) =>
        config["Database:OverridePath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "App_Data", "database.override");

    /// <summary>
    /// The saved connection, or null when there is none or it cannot be reached. An unreachable saved server must not stop the app from starting
    /// (nobody could then open the page to fix it), so the configured database is used instead, and the problem is reported.
    /// </summary>
    public static (string? Connection, DatabaseStartupState State) Load(IConfiguration config, ISecretProtector secrets)
    {
        var path = PathFor(config);
        if (!File.Exists(path)) return (null, new DatabaseStartupState(false, null));
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            var connection = secrets.Unprotect(saved?.Connection ?? "");
            if (string.IsNullOrWhiteSpace(connection)) return (null, new DatabaseStartupState(false, "The saved database settings could not be read (was the signing key changed?). The configured database is used."));
            var probe = new SqlConnectionStringBuilder(connection) { ConnectTimeout = 10 }.ConnectionString;
            using var c = new SqlConnection(probe);
            c.Open();
            return (connection, new DatabaseStartupState(true, null));
        }
        catch (Exception e)
        {
            return (null, new DatabaseStartupState(false, "The saved database server could not be reached (" + Short(e) + "). The configured database is used until it can."));
        }
    }

    public static void Save(IConfiguration config, ISecretProtector secrets, string connection)
    {
        var path = PathFor(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Saved(secrets.Protect(connection))));
    }

    public static void Clear(IConfiguration config)
    {
        var path = PathFor(config);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string Short(Exception e) => e is SqlException s ? s.Message.Split('\n')[0].Trim() : e.GetType().Name;
    private sealed record Saved(string Connection);
}

/// <summary>
/// Moves the app to another SQL Server. It checks the server, creates the database and any table that is missing (a table that is already there is
/// kept), copies every row the new server does not have yet (a row that is already there is left alone, a row that cannot be inserted is reported and
/// skipped, and the copy carries on), and only then, when the administrator says so, makes the app use the new server. Nothing is ever deleted or
/// changed on the server the app uses now.
/// </summary>
public sealed class DatabaseMoveService(IServiceProvider services, IConfiguration config, ISecretProtector secrets, DatabaseStartupState startup,
    IHostApplicationLifetime lifetime, ILogger<DatabaseMoveService> log)
{
    private static readonly Regex Name = new("^[A-Za-z0-9_\\-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex CreateTable = new(@"^\s*CREATE TABLE \[(?<s>[^\]]+)\]\.\[(?<t>[^\]]+)\]|^\s*CREATE TABLE \[(?<t2>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly object _gate = new();
    private MoveState _progress = new();
    private string? _readyConnection;          // the details of the last successful test or move, kept in memory only
    private string? _readyLabel;

    // ------------------------------------------------------------------ what the app uses now

    public DbInfoDto Info()
    {
        using var scope = services.CreateScope();
        var cs = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString() ?? "";
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            return new DbInfoDto(b.DataSource, b.InitialCatalog, "SQL Server", startup.UsingSavedServer, startup.Problem);
        }
        catch { return new DbInfoDto("(local file)", "", "SQLite", false, startup.Problem); }
    }

    // ------------------------------------------------------------------ test

    public async Task<DbTestDto> TestAsync(DbDetailsRequest d, CancellationToken ct)
    {
        var (cs, master, problem) = Build(d);
        if (problem is not null) return new DbTestDto(false, problem, false, [], []);
        try
        {
            await using var m = new SqlConnection(master);
            await m.OpenAsync(ct);
            var exists = await ScalarAsync(m, "SELECT CAST(DB_ID(@n) AS int)", ("@n", d.Database.Trim()), ct) is int;
            var wanted = ModelTables();
            if (!exists)
            {
                var canCreate = await ScalarAsync(m, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')", null, ct) is 1;
                Ready(cs, d);
                return new DbTestDto(true, canCreate
                    ? $"The server answered. The database {d.Database.Trim()} does not exist yet, so it will be created with all {wanted.Count} tables."
                    : $"The server answered, but the database {d.Database.Trim()} does not exist and this user may not create it. Ask for the database to be made, or for the right to make it.",
                    false, [], wanted.ToArray());
            }
            await using var c = new SqlConnection(cs);
            await c.OpenAsync(ct);
            var have = await TablesAsync(c, ct);
            var missing = wanted.Where(t => !have.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
            var existing = wanted.Where(t => have.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
            Ready(cs, d);
            return new DbTestDto(true, missing.Length == 0
                ? $"The server answered. All {wanted.Count} tables are already there; only rows that are missing will be added."
                : $"The server answered. {existing.Length} tables are already there and {missing.Length} will be created.", true, existing, missing);
        }
        catch (Exception e) { return new DbTestDto(false, Friendly(e), false, [], []); }
    }

    // ------------------------------------------------------------------ move

    public DbMoveDto Progress() => Snapshot();

    /// <summary>Starts the move in the background and returns at once; <see cref="Progress"/> shows how it is going.</summary>
    public DbMoveDto Start(DbDetailsRequest d)
    {
        var (cs, _, problem) = Build(d);
        if (problem is not null) throw new DomainException(problem, "invalid_database");
        using (var scope = services.CreateScope())
            if (SameServer(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString(), cs))
                throw new DomainException("That is the server the app uses now. Enter the new server.", "same_database");
        lock (_gate)
        {
            if (_progress.State == "running") throw new DomainException("A move is already running. Wait for it to finish.", "move_running");
            _progress = new MoveState { State = "running", Step = "Starting", StartedAt = DateTime.UtcNow, Target = $"{d.Server.Trim()} / {d.Database.Trim()}" };
        }
        _ = Task.Run(() => RunAsync(d, cs));
        return Snapshot();
    }

    private async Task RunAsync(DbDetailsRequest d, string target)
    {
        try
        {
            await EnsureDatabaseAsync(d);
            await EnsureTablesAsync(target);
            await CopyAsync(target);
            Ready(target, d);
            Update(p => { p.State = "done"; p.Step = "Finished"; p.FinishedAt = DateTime.UtcNow; });
        }
        catch (Exception e)
        {
            log.LogError(e, "Moving the database failed");
            Update(p => { p.State = "failed"; p.Step = "Stopped"; p.FinishedAt = DateTime.UtcNow; p.Notes.Add(Friendly(e)); });
        }
    }

    private async Task EnsureDatabaseAsync(DbDetailsRequest d)
    {
        Update(p => p.Step = "Checking the server");
        var (_, master, _) = Build(d);
        await using var m = new SqlConnection(master);
        await m.OpenAsync();
        if (await ScalarAsync(m, "SELECT CAST(DB_ID(@n) AS int)", ("@n", d.Database.Trim()), default) is int)
        {
            Update(p => p.Notes.Add($"The database {d.Database.Trim()} is already on the server; it is kept."));
            return;
        }
        Update(p => p.Step = "Creating the database");
        await using var cmd = m.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{d.Database.Trim()}]";      // the name was checked against a strict pattern
        cmd.CommandTimeout = 180;
        await cmd.ExecuteNonQueryAsync();
        Update(p => p.Notes.Add($"The database {d.Database.Trim()} was created."));
    }

    /// <summary>Makes every table that is missing, keeps every table that exists, and records the migrations so the app does not try to make them again.</summary>
    private async Task EnsureTablesAsync(string target)
    {
        Update(p => p.Step = "Making the missing tables");
        await using var db = TargetContext(target);
        var applied = new List<string>();
        try { applied = (await db.Database.GetAppliedMigrationsAsync()).ToList(); } catch (SqlException) { /* no history table yet */ }

        if (applied.Count > 0)
        {
            // an earlier copy of this app's database: bring it up to date the normal way, then fill in any table that was lost
            try
            {
                await db.Database.MigrateAsync();
                Update(p => p.Notes.Add("The database already belongs to this app; it was brought up to date."));
            }
            catch (SqlException e) { Update(p => p.Notes.Add("Bringing the existing tables up to date was skipped: " + Short(e))); }
        }

        var script = db.Database.GenerateCreateScript();
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        await using var c = new SqlConnection(target);
        await c.OpenAsync();
        int made = 0, kept = 0;
        foreach (var batch in batches)
        {
            var m = CreateTable.Match(batch);
            if (m.Success)
            {
                var table = m.Groups["t"].Success ? m.Groups["t"].Value : m.Groups["t2"].Value;
                if (await ScalarAsync(c, "SELECT OBJECT_ID(@t, 'U')", ("@t", "[dbo].[" + table + "]"), default) is int) { kept++; continue; }
            }
            try { await ExecAsync(c, batch); if (m.Success) made++; }
            catch (SqlException e) when (e.Number is 2714 or 1913 or 1779 or 1750 or 2705) { /* already there: an index, a key, a constraint */ }
            catch (SqlException e) { Update(p => p.Notes.Add("A step of making the tables was skipped: " + Short(e))); }
        }
        // the app's start-up would otherwise try to make the same tables again
        var history = db.GetService<IHistoryRepository>();
        await ExecAsync(c, history.GetCreateIfNotExistsScript());
        var version = (typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "9.0.0").Split('+')[0];
        var known = new HashSet<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT [MigrationId] FROM [__EFMigrationsHistory]";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) known.Add(r.GetString(0));
        }
        foreach (var id in db.Database.GetMigrations().Where(id => !known.Contains(id)))
            await ExecAsync(c, history.GetInsertScript(new HistoryRow(id, version)));
        Update(p => p.Notes.Add($"{made} tables were created and {kept} tables that were already there were kept."));
    }

    private async Task CopyAsync(string target)
    {
        List<Table> tables;
        await using (var model = TargetContext(target)) tables = Plan(model.Model);

        using var scope = services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetDbConnection();
        var close = source.State != ConnectionState.Open;
        if (close) await source.OpenAsync();
        try
        {
            await using var t = new SqlConnection(target);
            await t.OpenAsync();
            foreach (var table in tables)
            {
                Update(p => p.Step = $"Copying {table.Name}");
                var row = new TableProgress { Table = table.Name };
                Update(p => p.Tables.Add(row));
                try { await CopyTableAsync(source, t, table, row); }
                catch (Exception e) { Fail(row, "The whole table was skipped: " + Friendly(e)); }
            }
        }
        finally { if (close) await source.CloseAsync(); }
    }

    private static async Task CopyTableAsync(DbConnection source, SqlConnection target, Table table, TableProgress row)
    {
        var cols = string.Join(", ", table.Columns.Select(c => $"[{c.Name}]"));
        var tbl = $"[{table.Schema}].[{table.Name}]";

        await using (var count = source.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM [{table.Name}]";
            try { row.SourceRows = Convert.ToInt64(await count.ExecuteScalarAsync()); }
            catch (Exception) { row.SourceRows = 0; Fail(row, "This table is not in the database the app uses now; nothing to copy."); return; }
        }
        if (row.SourceRows == 0) return;

        await ExecAsync(target, "IF OBJECT_ID('tempdb..#stage') IS NOT NULL DROP TABLE #stage");
        await ExecAsync(target, $"SELECT {cols} INTO #stage FROM {tbl} WHERE 1 = 0");
        await using (var read = source.CreateCommand())
        {
            read.CommandText = $"SELECT {cols} FROM [{table.Name}]";
            await using var reader = await read.ExecuteReaderAsync();
            using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity, null) { DestinationTableName = "#stage", BulkCopyTimeout = 600, BatchSize = 1000 };
            foreach (var c in table.Columns) bulk.ColumnMappings.Add(c.Name, c.Name);
            await bulk.WriteToServerAsync(reader);
        }

        var identity = table.Columns.Any(c => c.Identity);
        var match = string.Join(" AND ", table.Key.Select(k => $"t.[{k}] = s.[{k}]"));
        var insertAll = $"INSERT INTO {tbl} ({cols}) SELECT {string.Join(", ", table.Columns.Select(c => $"s.[{c.Name}]"))} FROM #stage s WHERE NOT EXISTS (SELECT 1 FROM {tbl} t WHERE {match})";
        var sql = identity ? $"SET IDENTITY_INSERT {tbl} ON; {insertAll}; SET IDENTITY_INSERT {tbl} OFF;" : insertAll;
        try
        {
            row.Inserted = await ExecAsync(target, sql);
            row.AlreadyThere = row.SourceRows - row.Inserted;
        }
        catch (SqlException)
        {
            // one row is the trouble: go row by row so the others are still saved
            if (identity) { try { await ExecAsync(target, $"SET IDENTITY_INSERT {tbl} OFF"); } catch (SqlException) { } }
            await CopyRowByRowAsync(target, table, tbl, cols, identity, row);
        }
    }

    private static async Task CopyRowByRowAsync(SqlConnection target, Table table, string tbl, string cols, bool identity, TableProgress row)
    {
        var keys = new List<object?[]>();
        await using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = $"SELECT {string.Join(", ", table.Key.Select(k => $"[{k}]"))} FROM #stage";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) { var v = new object?[table.Key.Length]; r.GetValues(v!); keys.Add(v); }
        }
        var where = string.Join(" AND ", table.Key.Select((k, i) => $"s.[{k}] = @k{i}"));
        var match = string.Join(" AND ", table.Key.Select(k => $"t.[{k}] = s.[{k}]"));
        var insert = $"INSERT INTO {tbl} ({cols}) SELECT {string.Join(", ", table.Columns.Select(c => $"s.[{c.Name}]"))} FROM #stage s WHERE {where} AND NOT EXISTS (SELECT 1 FROM {tbl} t WHERE {match})";
        row.Inserted = 0; row.AlreadyThere = 0; row.Failed = 0;
        foreach (var key in keys)
        {
            await using var cmd = target.CreateCommand();
            cmd.CommandText = identity ? $"SET IDENTITY_INSERT {tbl} ON; {insert}; SET IDENTITY_INSERT {tbl} OFF;" : insert;
            for (var i = 0; i < key.Length; i++) cmd.Parameters.AddWithValue($"@k{i}", key[i] ?? DBNull.Value);
            try { if (await cmd.ExecuteNonQueryAsync() > 0) row.Inserted++; else row.AlreadyThere++; }
            catch (SqlException e)
            {
                if (identity) { try { await ExecAsync(target, $"SET IDENTITY_INSERT {tbl} OFF"); } catch (SqlException) { } }
                Fail(row, "A row was skipped: " + Short(e));
            }
        }
    }

    // ------------------------------------------------------------------ use the new server

    /// <summary>Saves the server that was last tested or moved to, so the app uses it from its next start.</summary>
    public DbUseDto Use()
    {
        string? cs; string? label;
        lock (_gate) { cs = _readyConnection; label = _readyLabel; }
        if (cs is null) throw new DomainException("Test the connection (or move the data) first, so the app knows which server to use.", "not_ready");
        if (Progress().State == "running") throw new DomainException("Wait for the move to finish first.", "move_running");
        DatabaseOverride.Save(config, secrets, cs);
        log.LogWarning("The database was changed to {Server}; the app uses it from its next start", label);
        // behind IIS the host starts the app again by itself on the next request; anywhere else somebody has to start it
        var iis = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"));
        if (iis) _ = Task.Run(async () => { await Task.Delay(1500); lifetime.StopApplication(); });
        return new DbUseDto(true, true, iis, iis
            ? "Saved. The app restarts by itself in a moment and then uses the new server. Open it again in a few seconds."
            : "Saved. Restart the app (stop it and start it again) and it will use the new server.");
    }

    /// <summary>Forgets the saved server, so the app uses the database it was installed with at its next start.</summary>
    public DbUseDto Forget()
    {
        DatabaseOverride.Clear(config);
        return new DbUseDto(true, true, false, "The saved server was removed. Restart the app and it will use the database it was installed with.");
    }

    // ------------------------------------------------------------------ helpers

    private (string Target, string Master, string? Problem) Build(DbDetailsRequest d)
    {
        string? bad = null;
        if (string.IsNullOrWhiteSpace(d.Server) || d.Server.Contains(';') || d.Server.Contains('=')) bad = "Enter the server address, for example 10.0.0.5,1433 or SERVER\\INSTANCE.";
        else if (string.IsNullOrWhiteSpace(d.Database) || !Name.IsMatch(d.Database.Trim())) bad = "The database name can use letters, digits, - and _ only.";
        else if (!d.WindowsLogin && (string.IsNullOrWhiteSpace(d.User) || d.User.Contains(';'))) bad = "Enter the user name.";
        else if (!d.WindowsLogin && string.IsNullOrEmpty(d.Password)) bad = "Enter the password.";
        if (bad is not null) return ("", "", bad);
        var b = new SqlConnectionStringBuilder
        {
            DataSource = d.Server.Trim(), InitialCatalog = d.Database.Trim(),
            Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 20, Pooling = false, ApplicationName = "FlowCapture",
        };
        if (d.WindowsLogin) b.IntegratedSecurity = true;          // the Windows account the app runs as (inside the hospital network)
        else { b.UserID = d.User.Trim(); b.Password = d.Password; }
        var master = new SqlConnectionStringBuilder(b.ConnectionString) { InitialCatalog = "master" };
        return (b.ConnectionString, master.ConnectionString, null);
    }

    private static bool SameServer(string? current, string target)
    {
        if (string.IsNullOrEmpty(current)) return false;
        try
        {
            var a = new SqlConnectionStringBuilder(current); var b = new SqlConnectionStringBuilder(target);
            return string.Equals(Norm(a.DataSource), Norm(b.DataSource), StringComparison.OrdinalIgnoreCase) && string.Equals(a.InitialCatalog, b.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        static string Norm(string s) => s.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private void Ready(string connection, DbDetailsRequest d)
    {
        lock (_gate) { _readyConnection = connection; _readyLabel = $"{d.Server.Trim()} / {d.Database.Trim()}"; }
    }

    private static AppDbContext TargetContext(string connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);

    private static List<string> ModelTables()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=.;Database=x").Options);
        return Plan(db.Model).Select(t => t.Name).ToList();
    }

    private sealed record Col(string Name, bool Identity);
    private sealed record Table(string Schema, string Name, List<Col> Columns, string[] Key, List<string> Needs);

    /// <summary>Every table, the ones other tables point to first.</summary>
    private static List<Table> Plan(IModel model)
    {
        var all = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
        foreach (var et in model.GetEntityTypes().Where(e => e.GetTableName() is not null && e.BaseType is null))
        {
            var name = et.GetTableName()!;
            if (all.ContainsKey(name) || et.FindPrimaryKey() is not { } pk) continue;
            var so = StoreObjectIdentifier.Table(name, et.GetSchema());
            var cols = et.GetProperties()
                .Where(p => p.GetColumnName(so) is not null && p.GetComputedColumnSql(so) is null)
                .Select(p => new Col(p.GetColumnName(so)!, p.GetValueGenerationStrategy(so) == SqlServerValueGenerationStrategy.IdentityColumn)).ToList();
            var needs = et.GetForeignKeys().Select(f => f.PrincipalEntityType.GetTableName()).Where(t => t is not null && !string.Equals(t, name, StringComparison.OrdinalIgnoreCase)).Select(t => t!).Distinct().ToList();
            all[name] = new Table(et.GetSchema() ?? "dbo", name, cols, pk.Properties.Select(p => p.GetColumnName(so)!).ToArray(), needs);
        }
        var ordered = new List<Table>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(Table t)
        {
            if (!seen.Add(t.Name)) return;
            foreach (var n in t.Needs) if (all.TryGetValue(n, out var d)) Visit(d);
            ordered.Add(t);
        }
        foreach (var t in all.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Visit(t);
        return ordered;
    }

    private static async Task<object?> ScalarAsync(SqlConnection c, string sql, (string, object)? p, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (p is { } x) cmd.Parameters.AddWithValue(x.Item1, x.Item2);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    private static async Task<int> ExecAsync(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> TablesAsync(SqlConnection c, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.tables";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    private static string Short(SqlException e) => e.Message.Split('\n')[0].Trim();

    /// <summary>What went wrong in words the administrator can act on. Never contains the password.</summary>
    public static string Friendly(Exception e)
    {
        if (e is SqlException s)
        {
            return s.Number switch
            {
                18456 => "The server refused the user name or password.",
                4060 => "The user cannot open that database.",
                262 or 229 or 297 => "The user is not allowed to do this. It needs the right to create the database and tables (for example db_owner).",
                -1 or 53 or 2 or 10060 or 10061 or 40 or 258 => "The server could not be reached. Check the address and port, that the server is running, and that a firewall allows this computer.",
                _ => Short(s),
            };
        }
        return e is OperationCanceledException or TimeoutException ? "The server did not answer in time." : e.Message;
    }

    // ------------------------------------------------------------------ progress

    private sealed class TableProgress
    {
        public string Table { get; set; } = "";
        public long SourceRows, Inserted, AlreadyThere, Failed;
        public List<string> Errors { get; } = [];
    }

    private sealed class MoveState
    {
        public string State { get; set; } = "idle";
        public string Step { get; set; } = "";
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Target { get; set; }
        public List<TableProgress> Tables { get; } = [];
        public List<string> Notes { get; } = [];
    }

    private static void Fail(TableProgress row, string message)
    {
        row.Failed++;
        if (row.Errors.Count < 3) row.Errors.Add(message);
    }

    private void Update(Action<MoveState> change) { lock (_gate) change(_progress); }

    private DbMoveDto Snapshot()
    {
        lock (_gate)
        {
            var p = _progress;
            return new DbMoveDto(p.State, p.Step, p.StartedAt, p.FinishedAt,
                p.Tables.Select(t => new DbTableProgressDto(t.Table, t.SourceRows, t.Inserted, t.AlreadyThere, t.Failed, t.Errors.ToArray())).ToArray(),
                p.Notes.ToArray(), p.State == "done" && _readyConnection is not null, p.Target);
        }
    }
}
