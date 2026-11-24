using FlowCapture.Application;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Persistence;
using FlowCapture.Infrastructure.Security;
using FlowCapture.Shared.Dtos;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>
/// Moving the app to another SQL Server: the missing tables are made, the data is copied, rows that are already there are left alone, a row that
/// cannot be inserted is skipped without stopping the rest. Uses two scratch databases on LocalDB (made and dropped here), never a real server.
/// </summary>
public class DatabaseMoveTests : IAsyncLifetime
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private readonly string _source = "FcMoveSrc" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _target = "FcMoveTgt" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _overridePath = Path.Combine(Path.GetTempPath(), "fc-override-" + Guid.NewGuid().ToString("N") + ".json");
    private bool _available;

    private static string Conn(string db) => $"Server={Server};Database={db};Trusted_Connection=True;TrustServerCertificate=True;Encrypt=True";
    private static AppDbContext Context(string db) => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(Conn(db)).Options);

    public async Task InitializeAsync()
    {
        try
        {
            await using var c = new SqlConnection(Conn("master"));
            await c.OpenAsync();
            _available = true;
        }
        catch (SqlException) { return; }
        await using var db = Context(_source);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        SqlConnection.ClearAllPools();
        await using var c = new SqlConnection(Conn("master"));
        await c.OpenAsync();
        foreach (var name in new[] { _source, _target })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
        if (File.Exists(_overridePath)) File.Delete(_overridePath);
    }

    private DatabaseMoveService Mover()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(Conn(_source)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:OverridePath"] = _overridePath }).Build();
        var secrets = new SecretProtector(Options.Create(new SecurityOptions { JwtSigningKey = "a-test-signing-key-of-more-than-32-characters" }));
        return new DatabaseMoveService(services.BuildServiceProvider(), config, secrets, new DatabaseStartupState(false, null), new FakeLifetime(), NullLogger<DatabaseMoveService>.Instance);
    }

    private DbDetailsRequest Details() => new(Server, _target, "", "", WindowsLogin: true);

    private static async Task<DbMoveDto> WaitAsync(DatabaseMoveService m)
    {
        for (var i = 0; i < 600; i++)
        {
            var p = m.Progress();
            if (p.State is "done" or "failed") return p;
            await Task.Delay(200);
        }
        throw new TimeoutException("The move did not finish.");
    }

    private static async Task<long> CountAsync(string db, string table)
    {
        await using var c = new SqlConnection(Conn(db));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task RunAsync(string db, string sql)
    {
        await using var c = new SqlConnection(Conn(db));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSourceAsync(string suffix, int clients = 2)
    {
        await using var db = Context(_source);
        var role = await db.Roles.FirstOrDefaultAsync() ?? new Role { Name = "Admin" };
        if (db.Entry(role).State == EntityState.Detached) db.Roles.Add(role);
        var user = new User { Email = $"mover{suffix}@test.local", DisplayName = "Mover " + suffix, PasswordHash = "x" };
        user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.Users.Add(user);
        for (var i = 0; i < clients; i++)
        {
            var client = new Client { Name = $"Client {suffix} {i}", Location = "Chennai" };
            client.Users.Add(new ClientUser { ClientId = client.Id, UserId = user.Id });
            db.Clients.Add(client);
        }
        for (var i = 0; i < 5; i++) db.AuditLogs.Add(new AuditLog { At = DateTime.UtcNow, UserId = user.Id, Action = "Test", EntityType = "Thing", EntityId = i.ToString() });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Missing_tables_are_made_the_data_is_copied_and_running_it_again_adds_nothing_twice()
    {
        if (!_available) return;                       // no SQL Server on this machine: nothing to test against
        await SeedSourceAsync("a");
        var mover = Mover();

        var test = await mover.TestAsync(Details(), default);
        test.Ok.Should().BeTrue(test.Message);
        test.DatabaseExists.Should().BeFalse();
        test.MissingTables.Should().Contain(["Users", "Clients", "Projects", "AuditLogs"]);

        mover.Start(Details());
        var done = await WaitAsync(mover);
        done.State.Should().Be("done", string.Join(" | ", done.Notes));
        done.Tables.Should().OnlyContain(t => t.Failed == 0, "nothing should have been skipped");
        foreach (var table in new[] { "Users", "Clients", "ClientUsers", "AuditLogs", "Roles" })
            (await CountAsync(_target, table)).Should().Be(await CountAsync(_source, table), table);
        done.Tables.Single(t => t.Table == "AuditLogs").Inserted.Should().Be(5, "the identity column keeps its numbers");
        // the app would otherwise try to make the same tables again when it starts
        (await CountAsync(_target, "__EFMigrationsHistory")).Should().Be(Context(_target).Database.GetMigrations().Count());
        (await mover.TestAsync(Details(), default)).MissingTables.Should().BeEmpty();

        // again: every row is already there
        mover.Start(Details());
        var again = await WaitAsync(mover);
        again.State.Should().Be("done");
        again.Tables.Sum(t => t.Inserted).Should().Be(0);
        again.Tables.Single(t => t.Table == "Clients").AlreadyThere.Should().Be(2);
        (await CountAsync(_target, "AuditLogs")).Should().Be(5);
    }

    [Fact]
    public async Task A_row_that_cannot_be_inserted_is_reported_and_skipped_and_the_rest_is_still_copied_and_a_lost_table_is_made_again()
    {
        if (!_available) return;
        await SeedSourceAsync("b");
        var mover = Mover();
        mover.Start(Details());
        (await WaitAsync(mover)).State.Should().Be("done");

        // the new server gets a client with the same name as one that is about to arrive (names are unique), and loses a table
        await using (var src = Context(_source))
        {
            src.Clients.Add(new Client { Name = "Same name", Location = "A" });
            src.Clients.Add(new Client { Name = "Fresh one", Location = "B" });
            await src.SaveChangesAsync();
        }
        await RunAsync(_target, $"INSERT INTO [Clients] (Id, Name, Location, IsActive, CreatedAt, UpdatedAt, ConcurrencyStamp) VALUES (NEWID(), 'Same name', 'Z', 1, GETUTCDATE(), GETUTCDATE(), 'x')");
        await RunAsync(_target, "DROP TABLE [ClientUsers]");

        mover.Start(Details());
        var run = await WaitAsync(mover);
        run.State.Should().Be("done", "a bad row must not stop the move");
        var clients = run.Tables.Single(t => t.Table == "Clients");
        clients.Failed.Should().Be(1);
        clients.Errors.Should().NotBeEmpty();
        clients.Inserted.Should().Be(1, "the other new client is saved");
        (await CountAsync(_target, "ClientUsers")).Should().Be(await CountAsync(_source, "ClientUsers"), "the lost table was made again and filled");
    }

    [Fact]
    public async Task The_chosen_server_is_saved_protected_and_only_after_a_test_and_a_wrong_server_gives_a_plain_answer()
    {
        if (!_available) return;
        var mover = Mover();
        var early = () => mover.Use();
        early.Should().Throw<DomainException>().Which.Message.Should().Contain("Test the connection");

        var bad = await mover.TestAsync(new DbDetailsRequest("127.0.0.1,1", "x", "u", "secret-password"), default);
        bad.Ok.Should().BeFalse();
        bad.Message.Should().NotContain("secret-password");
        (await mover.TestAsync(new DbDetailsRequest("a;b", "x", "u", "p"), default)).Ok.Should().BeFalse();
        (await mover.TestAsync(new DbDetailsRequest(Server, "bad name!", "u", "p"), default)).Message.Should().Contain("database name");

        // an existing database, so that the saved server can really be reached when the app starts
        (await mover.TestAsync(new DbDetailsRequest(Server, _source, "", "", WindowsLogin: true), default)).Ok.Should().BeTrue();
        var saved = mover.Use();
        saved.Saved.Should().BeTrue();
        var text = File.ReadAllText(_overridePath);
        text.Should().NotContain(_source, "the saved server is protected, not readable");

        var secrets = new SecretProtector(Options.Create(new SecurityOptions { JwtSigningKey = "a-test-signing-key-of-more-than-32-characters" }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:OverridePath"] = _overridePath }).Build();
        var (connection, state) = DatabaseOverride.Load(config, secrets);
        state.UsingSavedServer.Should().BeTrue();
        connection.Should().Contain(_source);

        // a saved server that cannot be reached must not stop the app from starting
        DatabaseOverride.Save(config, secrets, "Server=127.0.0.1,1;Database=x;User Id=u;Password=p;Connect Timeout=2;Encrypt=True;TrustServerCertificate=True");
        var (none, fallback) = DatabaseOverride.Load(config, secrets);
        none.Should().BeNull();
        fallback.Problem.Should().Contain("could not be reached");

        mover.Forget();
        File.Exists(_overridePath).Should().BeFalse();
    }

    [Fact]
    public async Task The_server_the_app_uses_now_is_refused_as_a_target()
    {
        if (!_available) return;
        var mover = Mover();
        var same = () => mover.Start(new DbDetailsRequest(Server, _source, "", "", WindowsLogin: true));
        same.Should().Throw<DomainException>().Which.Message.Should().Contain("uses now");
        await Task.CompletedTask;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
