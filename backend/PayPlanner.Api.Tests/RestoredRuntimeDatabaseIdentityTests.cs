using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeDatabaseIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityIsTransactionalAndRejectsDifferentPreparedDatabase(bool postgres)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (postgres && string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        await using DbConnection connection = postgres
            ? new NpgsqlConnection(connectionString) : new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE "SystemSettings" (
                "Key" TEXT PRIMARY KEY, "Value" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
        var instanceId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection, instanceId, CancellationToken.None));
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await RestoredRuntimeDatabaseIdentity.StampAsync(connection, transaction, instanceId, CancellationToken.None);
            await transaction.CommitAsync();
        }
        await RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection, instanceId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection, Guid.NewGuid(), CancellationToken.None));
        var replacement = Guid.NewGuid();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await RestoredRuntimeDatabaseIdentity.StampAsync(connection, transaction, replacement, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        await RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection, instanceId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection, replacement, CancellationToken.None));
    }

    [Fact]
    public async Task SqliteVerificationNeverCreatesMissingDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-identity-{Guid.NewGuid():N}.sqlite");
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<SqliteException>(() => RestoredRuntimeDatabaseIdentity.VerifyAsync(
            Configuration(), new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, $"Data Source={path}"), CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OrdinaryModeDoesNotOpenDatabase()
    {
        var configuration = Configuration();
        configuration[RestoredRuntimeIsolation.EnabledKey] = "false";
        await RestoredRuntimeDatabaseIdentity.VerifyAsync(configuration, null!, CancellationToken.None);
    }

    [Theory]
    [InlineData("fixture.invalid")]
    [InlineData("localhost")]
    [InlineData("192.0.2.1")]
    public async Task PostgreSqlRejectsNonLoopbackHostBeforeConnecting(string host)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDatabaseIdentity.VerifyAsync(
            Configuration(), new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, $"Host={host};Database=fixture;Username=fixture"), CancellationToken.None));

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000000000000000000000000000")]
    public void InstanceIdMustBeExplicitAndNonEmpty(string? value)
    {
        var configuration = Configuration();
        configuration[RestoredRuntimeDatabaseIdentity.ConfigurationKey] = value;
        Assert.Throws<InvalidOperationException>(() => RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration));
    }

    private static IConfigurationRoot Configuration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = "true",
            [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = Guid.NewGuid().ToString("N"),
        }).Build();
}
