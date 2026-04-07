using Microsoft.Data.Sqlite;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DatabaseProviderResolverTests
{
    [Fact]
    public void ResolveKind_ReturnsPostgreSql_WhenProviderIsExplicitlyConfigured()
    {
        var result = DatabaseProviderResolver.ResolveKind("postgresql", "Data Source=payplanner.db");

        Assert.Equal(DatabaseProviderKind.PostgreSql, result);
    }

    [Fact]
    public void ResolveKind_DetectsPostgreSql_FromConnectionStringShape()
    {
        var result = DatabaseProviderResolver.ResolveKind(
            configuredProvider: null,
            connectionString: "Host=127.0.0.1;Port=5432;Database=payplanner;Username=app;Password=secret");

        Assert.Equal(DatabaseProviderKind.PostgreSql, result);
    }

    [Fact]
    public void NormalizeSqliteConnection_NormalizesRelativeDatasource_AndEnablesSafeDefaults()
    {
        var normalized = DatabaseProviderResolver.NormalizeSqliteConnection("Data Source=payplanner.db");
        var builder = new SqliteConnectionStringBuilder(normalized);

        Assert.True(Path.IsPathRooted(builder.DataSource));
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, builder.Mode);
        Assert.Equal(SqliteCacheMode.Shared, builder.Cache);
        Assert.True(builder.ForeignKeys);
        Assert.True(builder.Pooling);
        Assert.Equal(30, builder.DefaultTimeout);
    }
}
