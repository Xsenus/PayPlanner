using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace PayPlanner.Api.Services;

public static class RestoredRuntimeDatabaseIdentity
{
    public const string SettingKey = "restored.runtime.instanceId";
    public const string ConfigurationKey = "RestoredRuntimeIsolation:InstanceId";

    public static Guid RequireInstanceId(IConfiguration configuration)
    {
        if (!Guid.TryParseExact(configuration[ConfigurationKey], "N", out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Требуется идентификатор подготовленной восстановленной среды.");
        return id;
    }

    public static async Task StampAsync(DbConnection connection, DbTransaction transaction, Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("Идентификатор среды пуст.", nameof(instanceId));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "SystemSettings" ("Key", "Value", "CreatedAt", "UpdatedAt")
            VALUES (@key, @value, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value", "UpdatedAt" = CURRENT_TIMESTAMP;
            """;
        AddParameter(command, "@key", SettingKey);
        AddParameter(command, "@value", instanceId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task VerifyAsync(IConfiguration configuration, DatabaseProviderInfo provider,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false)) return;
        var expected = RequireInstanceId(configuration);
        if (provider.IsPostgreSql)
        {
            var settings = new NpgsqlConnectionStringBuilder(provider.ConnectionString);
            if (!System.Net.IPAddress.TryParse(settings.Host, out var address)
                || !System.Net.IPAddress.IsLoopback(address))
                throw new InvalidOperationException("Пробная PostgreSQL-база должна находиться на явном loopback IP.");
        }
        await using DbConnection connection = provider.IsSqlite
            ? new SqliteConnection(new SqliteConnectionStringBuilder(provider.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString())
            : new NpgsqlConnection(provider.ConnectionString);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await connection.OpenAsync(timeout.Token);
        await VerifyConnectionAsync(connection, expected, timeout.Token);
    }

    public static async Task VerifyConnectionAsync(DbConnection connection, Guid expected, CancellationToken cancellationToken)
    {
        if (expected == Guid.Empty) throw new ArgumentException("Идентификатор среды пуст.", nameof(expected));
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = @key;";
        AddParameter(command, "@key", SettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string text || !Guid.TryParseExact(text, "N", out var actual) || actual != expected)
            throw new InvalidOperationException("Выбранная база не соответствует подготовленной восстановленной среде.");
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
