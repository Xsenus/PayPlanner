using Microsoft.Data.Sqlite;

namespace PayPlanner.Api.Services;

public enum DatabaseProviderKind
{
    Sqlite,
    PostgreSql
}

public sealed class DatabaseProviderInfo
{
    public DatabaseProviderInfo(DatabaseProviderKind kind, string connectionString)
    {
        Kind = kind;
        ConnectionString = connectionString;
    }

    public DatabaseProviderKind Kind { get; }
    public string ConnectionString { get; }

    public bool IsSqlite => Kind == DatabaseProviderKind.Sqlite;
    public bool IsPostgreSql => Kind == DatabaseProviderKind.PostgreSql;
    public string Key => IsPostgreSql ? "postgresql" : "sqlite";
    public string DisplayName => IsPostgreSql ? "PostgreSQL" : "SQLite";
}

public static class DatabaseProviderResolver
{
    public static DatabaseProviderInfo Resolve(IConfiguration configuration)
    {
        var configuredProvider =
            Environment.GetEnvironmentVariable("PAYPLANNER_DB_PROVIDER")
            ?? configuration["Database:Provider"];

        var rawConnectionString = configuration.GetConnectionString("Default") ?? "Data Source=payplanner.db";
        var providerKind = ResolveKind(configuredProvider, rawConnectionString);
        var normalizedConnectionString = providerKind == DatabaseProviderKind.Sqlite
            ? NormalizeSqliteConnection(rawConnectionString)
            : rawConnectionString;

        return new DatabaseProviderInfo(providerKind, normalizedConnectionString);
    }

    public static DatabaseProviderKind ResolveKind(string? configuredProvider, string connectionString)
    {
        var normalizedProvider = (configuredProvider ?? string.Empty).Trim().ToLowerInvariant();

        if (normalizedProvider is "postgres" or "postgresql" or "npgsql")
        {
            return DatabaseProviderKind.PostgreSql;
        }

        if (normalizedProvider is "sqlite" or "sqlite3")
        {
            return DatabaseProviderKind.Sqlite;
        }

        return LooksLikePostgreSqlConnection(connectionString)
            ? DatabaseProviderKind.PostgreSql
            : DatabaseProviderKind.Sqlite;
    }

    public static string NormalizeSqliteConnection(string rawConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(rawConnectionString)
        {
            DefaultTimeout = 30,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true
        };

        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        }

        return builder.ToString();
    }

    private static bool LooksLikePostgreSqlConnection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var normalized = connectionString.Trim();

        return normalized.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Username=", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("User Id=", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Port=", StringComparison.OrdinalIgnoreCase);
    }
}
