using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeDataProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAlsoQualifiesPassportCiphertextOrRejectsUnavailableFieldKeys(bool missingKeys)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await fixture.SeedPassportAsync();
        var before = fixture.Hashes();
        var recovery = RestoredRuntimeDataProtection.ReadConfiguration(fixture.Configuration)!;
        using var restored = fixture.Provider(recovery.KeysPath, Fixture.Application, includeFieldEncryptor: !missingKeys);
        if (missingKeys)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDataProtection.VerifyAsync(
                restored, fixture.Configuration, fixture.Database, recovery, CancellationToken.None));
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("synthetic", error.ToString());
        }
        else Assert.Equal(16, await RestoredRuntimeDataProtection.VerifyAsync(restored, fixture.Configuration,
            fixture.Database, recovery, CancellationToken.None));
        Assert.Equal(before, fixture.Hashes());
    }

    [Fact]
    public async Task StartupQualificationReadsStoredValuesWithoutChangingDatabaseOrKeyFiles()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = fixture.Hashes();
        var config = RestoredRuntimeDataProtection.ReadConfiguration(fixture.Configuration)!;
        using var restored = fixture.Provider(config.KeysPath, config.Expected.ApplicationDiscriminator!);
        var count = await RestoredRuntimeDataProtection.VerifyAsync(restored, fixture.Configuration,
            fixture.Database, config, CancellationToken.None);
        Assert.Equal(15, count);
        Assert.Equal(before, fixture.Hashes());
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("purpose")]
    [InlineData("identity")]
    [InlineData("application")]
    [InlineData("keys")]
    [InlineData("metadata")]
    [InlineData("schema")]
    [InlineData("json")]
    [InlineData("missing-db")]
    [InlineData("remote-db")]
    [InlineData("unknown-version")]
    public async Task StartupQualificationRejectsUnrecoverableValuesWithoutPlaintextFallbackOrWrites(string failure)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var config = RestoredRuntimeDataProtection.ReadConfiguration(fixture.Configuration)!;
        if (failure is "ciphertext" or "purpose" or "identity" or "schema" or "json" or "unknown-version")
        {
            await using var connection = new SqliteConnection(fixture.Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = failure switch
            {
                "identity" => "UPDATE SystemSettings SET Value = 'wrong-instance' WHERE Key = 'restored.runtime.instanceId'",
                "schema" => "DROP TABLE MaxOutboxMessages",
                "json" => "UPDATE SystemSettings SET Value = '{bad-json-sensitive}' WHERE Key = 'integrations.s3.connections'",
                "unknown-version" => "UPDATE SystemSettings SET Value = 'protected:v2:sensitive-synthetic-value' WHERE Key = 'integrations.yandexDisk.oauthToken'",
                _ => "UPDATE MaxOutboxMessages SET ProtectedActionUrl = @value"
            };
            using var source = fixture.Provider(fixture.SourceKeys, Fixture.Application);
            command.Parameters.AddWithValue("@value", failure == "purpose"
                ? source.GetRequiredService<IDataProtectionProvider>().CreateProtector("wrong-purpose").Protect("sensitive-synthetic-value")
                : "sensitive-corrupt-ciphertext");
            var affected = await command.ExecuteNonQueryAsync();
            if (failure == "unknown-version")
            {
                Assert.Equal(1, affected);
                command.CommandText = "SELECT Value FROM SystemSettings WHERE Key = 'integrations.yandexDisk.oauthToken'";
                Assert.Equal("protected:v2:sensitive-synthetic-value", await command.ExecuteScalarAsync());
            }
        }
        if (failure == "keys")
            foreach (var file in Directory.GetFiles(config.KeysPath, "key-*.xml"))
                File.Move(file, Path.Combine(fixture.Root, Path.GetFileName(file)));
        if (failure == "missing-db") File.Move(Path.Combine(fixture.Runtime, "restored.sqlite"), Path.Combine(fixture.Root, "detached.sqlite"));
        var before = fixture.Hashes();
        using var restored = fixture.Provider(config.KeysPath, failure == "application" ? "wrong-application" : Fixture.Application);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDataProtection.VerifyAsync(
            restored, fixture.Configuration,
            failure == "remote-db" ? new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, "Host=fixture.invalid;Database=synthetic;Username=synthetic") : fixture.Database,
            failure == "metadata" ? null : config, CancellationToken.None));
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain(fixture.Root, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(before, fixture.Hashes());
    }

    [Theory]
    [InlineData("missing-artifact")]
    [InlineData("missing-keys")]
    [InlineData("foreign-id")]
    [InlineData("outside-keys")]
    [InlineData("null-metadata")]
    [InlineData("version")]
    [InlineData("malformed-json")]
    public async Task ConfigurationRejectsUnqualifiedArtifactsAndKeyLocations(string failure)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var config = (IConfigurationRoot)fixture.Configuration;
        switch (failure)
        {
            case "missing-artifact": config[RestoredRuntimeDataProtection.ArtifactPathKey] = null; break;
            case "missing-keys": config[RestoredRuntimeDataProtection.KeysPathKey] = null; break;
            case "outside-keys": config[RestoredRuntimeDataProtection.KeysPathKey] = fixture.SourceKeys; break;
            case "foreign-id": config[RestoredRuntimeDatabaseIdentity.ConfigurationKey] = Guid.NewGuid().ToString("N"); break;
            case "malformed-json": await File.WriteAllTextAsync(fixture.Artifact, "{sensitive-invalid}"); break;
            default:
                using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Artifact)))
                {
                    var values = json.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone());
                    values[failure == "version" ? "formatVersion" : "dataProtectionRecovery"] = failure == "version" ? 2 : null;
                    await File.WriteAllTextAsync(fixture.Artifact, JsonSerializer.Serialize(values));
                }
                break;
        }
        var before = fixture.Hashes();
        var error = Assert.Throws<InvalidOperationException>(() => RestoredRuntimeDataProtection.ReadConfiguration(fixture.Configuration));
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain(fixture.Root, error.ToString());
        Assert.Equal(before, fixture.Hashes());
    }

    [Fact]
    public async Task OrdinaryRuntimeDoesNotReadRecoveryPathsOrRequireKeys()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Null(RestoredRuntimeDataProtection.ReadConfiguration(configuration));
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(0, await RestoredRuntimeDataProtection.VerifyAsync(services, configuration,
            new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, "Data Source=never-opened.sqlite"), null, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledQualificationDoesNotOpenDatabaseOrChangeKeys()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = fixture.Hashes();
        var recovery = RestoredRuntimeDataProtection.ReadConfiguration(fixture.Configuration)!;
        using var services = fixture.Provider(recovery.KeysPath, Fixture.Application);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RestoredRuntimeDataProtection.VerifyAsync(
            services, fixture.Configuration, fixture.Database, recovery, cancellation.Token));
        Assert.Equal(before, fixture.Hashes());
    }

    private sealed class Fixture : IDisposable
    {
        public const string Application = "PayPlanner.RestoredStartupSyntheticFixture";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pp-key-startup-" + Guid.NewGuid().ToString("N"));
        public string SourceKeys => Path.Combine(Root, "source-keys");
        public string Runtime => Path.Combine(Root, "runtime");
        public string Keys => Path.Combine(Runtime, "recovery-keys");
        public string Artifact => Path.Combine(Runtime, "restored-storage-paths.json");
        public DatabaseProviderInfo Database => new(DatabaseProviderKind.Sqlite, $"Data Source={Path.Combine(Runtime, "restored.sqlite")};Pooling=False");
        public IConfiguration Configuration { get; }
        private readonly Guid _instanceId = Guid.NewGuid();

        public Fixture()
        {
            Directory.CreateDirectory(SourceKeys);
            Directory.CreateDirectory(Keys);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RestoredRuntimeIsolation.EnabledKey] = "true",
                [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = _instanceId.ToString("N"),
                [RestoredRuntimeDataProtection.ArtifactPathKey] = Artifact,
                [RestoredRuntimeDataProtection.KeysPathKey] = Keys,
            }).Build();
        }

        public ServiceProvider Provider(string path, string application, bool disableKeyGeneration = true, bool includeFieldEncryptor = false)
        {
            var services = new ServiceCollection().AddLogging();
            var builder = services.AddDataProtection().SetApplicationName(application).PersistKeysToFileSystem(new DirectoryInfo(path));
            if (disableKeyGeneration) builder.DisableAutomaticKeyGeneration();
            services.AddSingleton<DataProtectionRecoveryInventory>();
            if (includeFieldEncryptor) services.AddSingleton<IFieldEncryptor>(new AeadFieldEncryptor(new SyntheticPassportKeys()));
            return services.BuildServiceProvider();
        }

        public async Task SeedPassportAsync()
        {
            var encryptor = new AeadFieldEncryptor(new SyntheticPassportKeys());
            var ciphertext = await encryptor.EncryptAsync("synthetic-passport", "ClientPassport:IssuedBy:1", CancellationToken.None);
            await using var connection = new SqliteConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ClientPassports (Id, IssuedByEncrypted) VALUES (1, @ciphertext)";
            command.Parameters.AddWithValue("@ciphertext", ciphertext);
            await command.ExecuteNonQueryAsync();
        }

        public async Task PrepareAsync()
        {
            using var source = Provider(SourceKeys, Application, disableKeyGeneration: false);
            var protection = source.GetRequiredService<IDataProtectionProvider>();
            await using var connection = new SqliteConnection(Database.ConnectionString);
            await connection.OpenAsync();
            var tables = new (string Table, string[] Columns, string Purpose)[]
            {
                ("ElectronicSignatureSigners", ["ProtectedContact"], "PayPlanner.ElectronicSignature.Contact.v1"),
                ("MessengerRecipientBindings", ["ProtectedAddress"], "PayPlanner.MessengerDocumentDelivery.Address.v1"),
                ("MessengerDocumentDeliveries", ["ProtectedLinkToken"], "PayPlanner.MessengerDocumentDelivery.Address.v1"),
                ("GoogleCalendarConnections", ["AccessTokenProtected", "RefreshTokenProtected"], "PayPlanner.GoogleCalendar.Tokens.v1"),
                ("YandexCalendarConnections", ["AccessTokenProtected", "RefreshTokenProtected"], "PayPlanner.YandexCalendar.Tokens.v1"),
                ("MaxInboundCorrespondenceMessages", ["ProtectedText"], "PayPlanner.Notifications.Max.Correspondence.v1"),
                ("MaxOutboxMessages", ["ProtectedText", "ProtectedActionUrl"], "PayPlanner.Notifications.Max.Outbox.v1"),
            };
            foreach (var (table, columns, purpose) in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TABLE {table} ({string.Join(", ", columns.Select(column => column + " TEXT"))}); INSERT INTO {table} VALUES ({string.Join(", ", columns.Select((_, index) => "@v" + index))});";
                for (var index = 0; index < columns.Length; index++)
                    command.Parameters.AddWithValue("@v" + index, protection.CreateProtector(purpose).Protect("Синтетическая запись Ω"));
                await command.ExecuteNonQueryAsync();
            }
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE SystemSettings (Key TEXT PRIMARY KEY, Value TEXT);
                    CREATE TABLE ClientPassports (Id INTEGER PRIMARY KEY, SeriesEncrypted TEXT, NumberEncrypted TEXT,
                        IssuedByEncrypted TEXT, IssuedAtEncrypted TEXT, DepartmentCodeEncrypted TEXT,
                        BirthPlacePassportEncrypted TEXT, BirthPlaceFiasEncrypted TEXT, SeriesBlindIndex TEXT, NumberBlindIndex TEXT);
                    """;
                await create.ExecuteNonQueryAsync();
            }
            var settings = new Dictionary<string, string>
            {
                [RestoredRuntimeDatabaseIdentity.SettingKey] = _instanceId.ToString("N"),
                ["integrations.s3.connections"] = JsonSerializer.Serialize(new[] { new { secretAccessKey = "protected:v1:" + protection.CreateProtector("PayPlanner.S3Storage.Secrets.v1").Protect("synthetic-s3") } }),
                ["integrations.yandexDisk.connections"] = JsonSerializer.Serialize(new[] { new { oAuthToken = "protected:v1:" + protection.CreateProtector("PayPlanner.YandexDisk.Tokens.v1").Protect("synthetic-disk") } }),
                ["integrations.yandexDisk.oauthToken"] = "protected:v1:" + protection.CreateProtector("PayPlanner.YandexDisk.Tokens.v1").Protect("synthetic-disk-old"),
                ["notifications.max.botToken"] = "dp:v1:" + protection.CreateProtector("PayPlanner.Notifications.Max.Secrets.v1").Protect("synthetic-max"),
                ["notifications.sms.apiToken"] = "dp:v1:" + protection.CreateProtector("PayPlanner.Notifications.Sms.ApiToken.v1").Protect("synthetic-sms"),
            };
            foreach (var (key, value) in settings)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO SystemSettings VALUES (@key, @value)";
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                await command.ExecuteNonQueryAsync();
            }
            var expected = source.GetRequiredService<DataProtectionRecoveryInventory>().Capture();
            foreach (var file in Directory.GetFiles(SourceKeys, "key-*.xml")) File.Copy(file, Path.Combine(Keys, Path.GetFileName(file)));
            await File.WriteAllTextAsync(Artifact, JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                runtimeInstanceId = _instanceId.ToString("N"),
                completeRuntimeConfiguration = false,
                dataProtectionRecovery = expected,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public string[] Hashes() => Directory.GetFiles(Runtime, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(Runtime, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("pp-key-startup-", Path.GetFileName(resolved));
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class SyntheticPassportKeys : IFieldEncryptionKeyStore
    {
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync("synthetic", ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (keyId != "synthetic") throw new InvalidOperationException();
            return ValueTask.FromResult(new FieldEncryptionKeyMaterial(keyId,
                Enumerable.Repeat((byte)7, 32).ToArray(), Enumerable.Repeat((byte)9, 32).ToArray()));
        }
    }
}
