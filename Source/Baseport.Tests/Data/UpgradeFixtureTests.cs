using System.Text.Json;
using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public sealed class UpgradeFixtureTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "upgrades");

    public static TheoryData<string> Releases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Fixtures, "*.db").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    private sealed record Credentials(string Username, string Password);

    private sealed record ApiAccount(string Username, string Token);

    private sealed record Expected(Credentials Admin, ApiAccount ApiAccount, Dictionary<string, int> Counts, Dictionary<string, string> Records);

    [Fact]
    public void AtLeastOneFixture() => Assert.NotEmpty(Releases());

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task ReleaseUpgrades(string release)
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = JsonSerializer.Deserialize<Expected>(
            await File.ReadAllTextAsync(Path.Combine(Fixtures, release + ".expected.json"), ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })!;
        var dir = Path.Combine(Path.GetTempPath(), "baseport-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "baseport.db");
        File.Copy(Path.Combine(Fixtures, release + ".db"), file);
        try
        {
            using (var db = TestDb.Open($"Data Source={file};Pooling=False"))
            {
                await SchemaBootstrap.ApplyAsync(db);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));

                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync(ct);
                foreach (var (table, count) in expected.Counts)
                {
                    Assert.Matches("^_[a-z_]+$", table);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT count(*) FROM \"{table}\"";
                    Assert.Equal(count, Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)));
                }

                foreach (var (name, id) in expected.Records)
                {
                    var table = await db.Tables.Include(t => t.Fields).SingleAsync(t => t.Name == name, ct);
                    var page = await QueryEngine.ListAsync(db, table, [.. table.Fields], null, false, null, 1, 500, ct: TestContext.Current.CancellationToken);
                    Assert.Contains(page.Records, r => r.Id == id);
                }

                var admin = await db.UserAccounts.SingleAsync(u => u.Username == expected.Admin.Username, ct);
                Assert.True(AdminAuth.VerifyPassword(expected.Admin.Password, admin.PasswordHash));
                var api = await db.UserAccounts.SingleAsync(u => u.Username == expected.ApiAccount.Username, ct);
                Assert.Equal(ApiAuth.HashToken(expected.ApiAccount.Token), api.ApiTokenHash);
            }

            Assert.True((await DatabaseIntegrity.CheckFileAsync(file, ct: ct)).Ok);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
