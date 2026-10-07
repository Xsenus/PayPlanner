using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeFieldEncryptionTests
{
    [Fact]
    public async Task ReadsAllFieldsAndHistoricalIndexesWithoutActiveKeyOrDatabaseWrites()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = fixture.Hash();
        await using var connection = await fixture.OpenReadOnlyAsync();
        using var services = fixture.Services();
        Assert.Equal(7, await RestoredRuntimeFieldEncryption.VerifyConnectionAsync(connection, services, CancellationToken.None));
        Assert.Equal(before, fixture.Hash());
        Assert.Equal(0, fixture.Keys.ActiveReads);
        Assert.Equal(9, fixture.Keys.StoredReads);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("blank")]
    [InlineData("version")]
    [InlineData("field-context")]
    [InlineData("record-context")]
    [InlineData("missing-key")]
    [InlineData("wrong-master")]
    [InlineData("wrong-blind-key")]
    [InlineData("index")]
    [InlineData("missing-index")]
    [InlineData("orphan-index")]
    [InlineData("schema")]
    [InlineData("large")]
    [InlineData("key-provider-denied")]
    public async Task RejectsUnreadableFieldsAndIndexesWithoutLeakingOrChangingRecords(string failure)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        if (failure == "missing-key") fixture.Keys.RemoveHistorical = true;
        else if (failure == "wrong-master") fixture.Keys.WrongMaster = true;
        else if (failure == "wrong-blind-key") fixture.Keys.WrongBlind = true;
        else if (failure == "key-provider-denied") fixture.Keys.Denied = true;
        else
        {
            await using var source = new SqliteConnection($"Data Source={fixture.Database};Pooling=False");
            await source.OpenAsync();
            await using var edit = source.CreateCommand();
            edit.CommandText = failure switch
            {
                "field-context" => "UPDATE ClientPassports SET NumberEncrypted = SeriesEncrypted",
                "record-context" => "UPDATE ClientPassports SET Id = 2",
                "index" => "UPDATE ClientPassports SET SeriesBlindIndex = @value",
                "missing-index" => "UPDATE ClientPassports SET SeriesBlindIndex = NULL",
                "orphan-index" => "UPDATE ClientPassports SET SeriesEncrypted = NULL",
                "schema" => "DROP TABLE ClientPassports",
                _ => "UPDATE ClientPassports SET IssuedByEncrypted = @value",
            };
            edit.Parameters.AddWithValue("@value", failure switch
            {
                "blank" => "",
                "version" => "ppenc:v2:synthetic-sensitive-value",
                "index" => new string('0', 64),
                "large" => new string('x', 65537),
                _ => "synthetic-sensitive-corruption",
            });
            await edit.ExecuteNonQueryAsync();
        }
        var before = fixture.Hash();
        await using var connection = await fixture.OpenReadOnlyAsync();
        using var services = fixture.Services();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeFieldEncryption.VerifyConnectionAsync(connection, services, CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString());
        Assert.DoesNotContain(fixture.Root, error.ToString());
        Assert.Equal(before, fixture.Hash());
    }

    [Fact]
    public async Task LegacyPlaintextAndEmptyColumnsDoNotRequireOrCallKeyStore()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await using (var source = new SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            await source.OpenAsync();
            await using var clear = source.CreateCommand();
            clear.CommandText = "DELETE FROM ClientPassports; INSERT INTO ClientPassports (Id, Series) VALUES (3, 'synthetic-legacy-plaintext');";
            await clear.ExecuteNonQueryAsync();
        }
        var before = fixture.Hash();
        await using var connection = await fixture.OpenReadOnlyAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(0, await RestoredRuntimeFieldEncryption.VerifyConnectionAsync(connection, services, CancellationToken.None));
        Assert.Equal(before, fixture.Hash());
    }

    [Fact]
    public async Task CancellationIsNotReplacedBySuccessOrAnUnreadableDataError()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = fixture.Hash();
        await using var connection = await fixture.OpenReadOnlyAsync();
        using var services = fixture.Services();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RestoredRuntimeFieldEncryption.VerifyConnectionAsync(connection, services, cancellation.Token));
        Assert.Equal(0, fixture.Keys.StoredReads);
        Assert.Equal(before, fixture.Hash());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pp-field-recovery-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "synthetic.sqlite");
        public SyntheticKeys Keys { get; } = new();
        public Fixture() => Directory.CreateDirectory(Root);
        public ServiceProvider Services() => new ServiceCollection().AddSingleton<IFieldEncryptor>(new AeadFieldEncryptor(Keys)).BuildServiceProvider();
        public byte[] Hash() => SHA256.HashData(File.ReadAllBytes(Database));

        public async Task<SqliteConnection> OpenReadOnlyAsync()
        {
            var connection = new SqliteConnection($"Data Source={Database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            return connection;
        }

        public async Task PrepareAsync()
        {
            var encryptor = new AeadFieldEncryptor(Keys);
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ClientPassports (Id INTEGER PRIMARY KEY, Series TEXT, SeriesEncrypted TEXT, NumberEncrypted TEXT,
                    IssuedByEncrypted TEXT, IssuedAtEncrypted TEXT, DepartmentCodeEncrypted TEXT,
                    BirthPlacePassportEncrypted TEXT, BirthPlaceFiasEncrypted TEXT, SeriesBlindIndex TEXT, NumberBlindIndex TEXT);
                INSERT INTO ClientPassports (Id, SeriesEncrypted, NumberEncrypted, IssuedByEncrypted, IssuedAtEncrypted,
                    DepartmentCodeEncrypted, BirthPlacePassportEncrypted, BirthPlaceFiasEncrypted, SeriesBlindIndex, NumberBlindIndex)
                    VALUES (1, @v0, @v1, @v2, @v3, @v4, @v5, @v6, @i0, @i1);
                """;
            var fields = new[] { "Series", "Number", "IssuedBy", "IssuedAt", "DepartmentCode", "BirthPlacePassport", "BirthPlaceFias" };
            for (var index = 0; index < fields.Length; index++)
            {
                var value = "Синтетическое значение Ω " + fields[index];
                command.Parameters.AddWithValue("@v" + index, await encryptor.EncryptAsync(value, $"ClientPassport:{fields[index]}:1", CancellationToken.None));
                if (index < 2) command.Parameters.AddWithValue("@i" + index, await encryptor.ComputeBlindIndexAsync(value, $"ClientPassport:{fields[index]}", CancellationToken.None));
            }
            await command.ExecuteNonQueryAsync();
            Keys.Active = "new";
            Keys.ActiveReads = 0;
            Keys.StoredReads = 0;
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("pp-field-recovery-", Path.GetFileName(resolved));
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class SyntheticKeys : IFieldEncryptionKeyStore
    {
        private readonly byte[] _master = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _blind = RandomNumberGenerator.GetBytes(32);
        public string Active = "old";
        public int ActiveReads;
        public int StoredReads;
        public bool RemoveHistorical;
        public bool WrongMaster;
        public bool WrongBlind;
        public bool Denied;

        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct)
        {
            ActiveReads++;
            return GetKeyAsync(Active, ct);
        }

        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            StoredReads++;
            if (Denied) throw new UnauthorizedAccessException("synthetic-provider-secret-detail");
            if (keyId != "old" || RemoveHistorical) throw new InvalidOperationException("synthetic-key-unavailable");
            return ValueTask.FromResult(new FieldEncryptionKeyMaterial(keyId,
                WrongMaster ? RandomNumberGenerator.GetBytes(32) : (byte[])_master.Clone(),
                WrongBlind ? RandomNumberGenerator.GetBytes(32) : (byte[])_blind.Clone()));
        }
    }
}
