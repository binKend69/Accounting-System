using HisabTak.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute(bool recovery = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HISABTAK_TEST_SQL_CONNECTION")))
            Skip = "SQL Server integration is not configured. Set HISABTAK_TEST_SQL_CONNECTION with certificate verification.";
        else if (recovery && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HISABTAK_TEST_SQL_DATA_DIRECTORY")))
            Skip = "Server-side backup/restore paths are not configured.";
    }
}

internal sealed class SqlTestDatabase : IAsyncDisposable
{
    private readonly string _administrativeConnection;
    private readonly HashSet<string> _ownedDatabases = [];
    public string Name { get; }
    public string ConnectionString { get; }

    private SqlTestDatabase()
    {
        var raw = Environment.GetEnvironmentVariable("HISABTAK_TEST_SQL_CONNECTION")
            ?? throw new InvalidOperationException("SQL integration connection is required.");
        var builder = new SqlConnectionStringBuilder(raw) { InitialCatalog = "master", Pooling = false };
        if (builder.TrustServerCertificate || builder.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidOperationException("Tests require SQL certificate verification.");
        _administrativeConnection = builder.ConnectionString;
        Name = "HisabTak_Test_" + Guid.NewGuid().ToString("N");
        _ownedDatabases.Add(Name);
        builder.InitialCatalog = Name;
        ConnectionString = builder.ConnectionString;
    }

    public static async Task<SqlTestDatabase> CreateAsync(string? migration = null)
    {
        var test = new SqlTestDatabase();
        try
        {
            await using var database = test.Context();
            if (migration is null) await database.Database.MigrateAsync();
            else await database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(migration);
            return test;
        }
        catch { await test.DisposeAsync(); throw; }
    }

    public HisabTakDbContext Context(string? target = null)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (target is not null) builder.InitialCatalog = target;
        return new HisabTakDbContext(new DbContextOptionsBuilder<HisabTakDbContext>()
            .UseSqlServer(builder.ConnectionString, sql => sql.UseCompatibilityLevel(160)).Options);
    }

    public ServiceProvider Services() => new ServiceCollection().AddSqlServerFoundation(ConnectionString)
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    public string ReserveRestoreTarget()
    {
        var name = "HisabTak_Test_" + Guid.NewGuid().ToString("N");
        _ownedDatabases.Add(name);
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new SqlConnection(_administrativeConnection);
        await connection.OpenAsync();
        foreach (var name in _ownedDatabases)
        {
            // Only generated databases owned by this test may be dropped.
            if (!name.StartsWith("HisabTak_Test_", StringComparison.Ordinal)) throw new InvalidOperationException();
            await using var command = new SqlCommand($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection);
            command.Parameters.AddWithValue("@name", name);
            await command.ExecuteNonQueryAsync();
        }
    }
}
