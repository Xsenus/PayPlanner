using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace PayPlanner.Api.Services;

public sealed record RestoredDataProtectionConfiguration(DataProtectionRecoveryMetadata Expected, string KeysPath);

/// <summary>Local startup qualification; never exports keys, writes restored records, or calls integrations.</summary>
public static class RestoredRuntimeDataProtection
{
    public const string ArtifactPathKey = "RestoredRuntimeIsolation:StorageSettingsPath";
    public const string KeysPathKey = "RestoredRuntimeIsolation:KeysPath";
    private const string Failure = "Не подтверждено чтение защищённых данных восстановленной CRM. Проверьте комплект и отдельно восстановленные ключи.";

    public static RestoredDataProtectionConfiguration? ReadConfiguration(IConfiguration configuration)
    {
        if (!configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false)) return null;
        try
        {
            var artifactPath = RequireExistingPhysicalPath(configuration[ArtifactPathKey], directory: false);
            var keysPath = RequireExistingPhysicalPath(configuration[KeysPathKey], directory: true);
            var root = Path.GetDirectoryName(artifactPath)!;
            var relative = Path.GetRelativePath(root, keysPath);
            if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException();
            using var input = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) throw new InvalidDataException();
            using var artifact = JsonDocument.Parse(input);
            var json = artifact.RootElement;
            if (json.GetProperty("formatVersion").GetInt32() != 1
                || json.GetProperty("runtimeInstanceId").GetString() != RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration).ToString("N"))
                throw new InvalidDataException();
            var expected = json.GetProperty("dataProtectionRecovery").Deserialize<DataProtectionRecoveryMetadata>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException();
            DataProtectionRecoveryInventory.ValidateMetadata(expected);
            return new(expected, keysPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // No key material, ciphertext, path, or low-level exception is exposed in startup logs.
            throw new InvalidOperationException(Failure);
        }
    }

    private static string RequireExistingPhysicalPath(string? path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        var resolved = Path.GetFullPath(path);
        if (directory ? !Directory.Exists(resolved) : !File.Exists(resolved)) throw new InvalidDataException();
        for (var current = resolved; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        if (directory)
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(resolved))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(file)) throw new InvalidDataException();
        }
        return resolved;
    }

    public static async Task<long> VerifyAsync(IServiceProvider services, IConfiguration configuration,
        DatabaseProviderInfo database, RestoredDataProtectionConfiguration? recovery, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false)) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (recovery is null) throw new InvalidOperationException(Failure);
        try
        {
            DataProtectionRecoveryInventory.RequireCompatible(recovery.Expected,
                services.GetRequiredService<DataProtectionRecoveryInventory>().Capture());
            foreach (var key in services.GetRequiredService<IKeyManager>().GetAllKeys())
                if (key.CreateEncryptor() is null) throw new CryptographicException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            if (database.IsPostgreSql && (!System.Net.IPAddress.TryParse(new NpgsqlConnectionStringBuilder(database.ConnectionString).Host, out var address)
                || !System.Net.IPAddress.IsLoopback(address))) throw new InvalidOperationException(Failure);
            await using DbConnection connection = database.IsSqlite
                ? new SqliteConnection(new SqliteConnectionStringBuilder(database.ConnectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString())
                : new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var transaction = database.IsPostgreSql
                ? await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token) : null;
            if (transaction is not null)
            {
                await using var readOnly = connection.CreateCommand();
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(timeout.Token);
            }
            // The identity check also covers direct callers outside Program.
            await RestoredRuntimeDatabaseIdentity.VerifyConnectionAsync(connection,
                RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration), timeout.Token);
            var count = await VerifyProtectedRecordsAsync(connection, services.GetRequiredService<IDataProtectionProvider>(), timeout.Token);
            return count + await RestoredRuntimeFieldEncryption.VerifyConnectionAsync(connection, services, timeout.Token);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException or InvalidOperationException or DbException or JsonException or FormatException)
        {
            throw new InvalidOperationException(Failure);
        }
    }

    private static readonly (string Table, string[] Columns, string Purpose)[] ProtectedTables =
    [
        ("ElectronicSignatureSigners", ["ProtectedContact"], "PayPlanner.ElectronicSignature.Contact.v1"),
        ("MessengerRecipientBindings", ["ProtectedAddress"], "PayPlanner.MessengerDocumentDelivery.Address.v1"),
        ("MessengerDocumentDeliveries", ["ProtectedLinkToken"], "PayPlanner.MessengerDocumentDelivery.Address.v1"),
        ("GoogleCalendarConnections", ["AccessTokenProtected", "RefreshTokenProtected"], "PayPlanner.GoogleCalendar.Tokens.v1"),
        ("YandexCalendarConnections", ["AccessTokenProtected", "RefreshTokenProtected"], "PayPlanner.YandexCalendar.Tokens.v1"),
        ("MaxInboundCorrespondenceMessages", ["ProtectedText"], "PayPlanner.Notifications.Max.Correspondence.v1"),
        ("MaxOutboxMessages", ["ProtectedText", "ProtectedActionUrl"], "PayPlanner.Notifications.Max.Outbox.v1"),
    ];

    private static async Task<long> VerifyProtectedRecordsAsync(DbConnection connection, IDataProtectionProvider provider, CancellationToken token)
    {
        long checkedValues = 0;
        foreach (var (table, columns, purpose) in ProtectedTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 120;
            // Identifiers are fixed application constants, never user input.
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"\"{column}\""))} FROM \"{table}\"";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            var protector = provider.CreateProtector(purpose);
            while (await reader.ReadAsync(token))
                for (var index = 0; index < columns.Length; index++)
                    if (!reader.IsDBNull(index))
                    {
                        var ciphertext = reader.GetString(index);
                        if (string.IsNullOrWhiteSpace(ciphertext)) continue;
                        _ = protector.Unprotect(ciphertext);
                        checkedValues++;
                    }
        }
        await using var settings = connection.CreateCommand();
        settings.CommandTimeout = 120;
        settings.CommandText = """
            SELECT "Key", "Value" FROM "SystemSettings" WHERE "Key" IN (
                'integrations.s3.connections', 'integrations.yandexDisk.connections', 'integrations.yandexDisk.oauthToken',
                'notifications.max.botToken', 'notifications.max.webhookSecret', 'notifications.sms.apiToken')
            """;
        await using var settingsReader = await settings.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await settingsReader.ReadAsync(token))
        {
            var key = settingsReader.GetString(0);
            if (settingsReader.IsDBNull(1)) continue;
            var value = settingsReader.GetString(1);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var s3 = key == "integrations.s3.connections";
            var yandex = key.StartsWith("integrations.yandexDisk.", StringComparison.Ordinal);
            var purpose = s3 ? "PayPlanner.S3Storage.Secrets.v1" : yandex ? "PayPlanner.YandexDisk.Tokens.v1"
                : key == "notifications.sms.apiToken" ? "PayPlanner.Notifications.Sms.ApiToken.v1" : "PayPlanner.Notifications.Max.Secrets.v1";
            var prefix = s3 || yandex ? "protected:v1:" : "dp:v1:";
            var protector = provider.CreateProtector(purpose);
            if (key.EndsWith(".connections", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(value);
                foreach (var item in json.RootElement.EnumerateArray())
                    foreach (var property in item.EnumerateObject())
                        if (string.Equals(property.Name, s3 ? "secretAccessKey" : "oAuthToken", StringComparison.OrdinalIgnoreCase))
                            checkedValues += VerifyPrefixed(protector, prefix, property.Value.GetString()) ? 1 : 0;
            }
            else checkedValues += VerifyPrefixed(protector, prefix, value) ? 1 : 0;
        }
        return checkedValues;
    }

    private static bool VerifyPrefixed(IDataProtector protector, string prefix, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return false;
        // Legacy plaintext settings are not migrated or claimed as protected values here.
        if (!protectedValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (protectedValue.StartsWith("protected:", StringComparison.Ordinal) || protectedValue.StartsWith("dp:", StringComparison.Ordinal))
                throw new CryptographicException();
            return false;
        }
        _ = protector.Unprotect(protectedValue[prefix.Length..]);
        return true;
    }
}
