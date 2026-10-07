using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Portable synthetic key-ring qualification only; never loads application or production keys.</summary>
public sealed class RestoredDataProtectionRecoveryTests
{
    private const string Application = "PayPlanner.SyntheticRecoveryFixture";

    [Fact]
    public void InventoryReadsEffectiveDiscriminatorWithoutGeneratingOrChangingKeys()
    {
        using var fixture = new Fixture();
        using var source = Provider(fixture.Original, Application);
        var inventory = source.GetRequiredService<DataProtectionRecoveryInventory>();
        Assert.Empty(inventory.Capture().KeyIds);
        Assert.Empty(Directory.GetFiles(fixture.Original));
        source.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("synthetic");
        var before = Hashes(fixture.Original);
        var snapshot = inventory.Capture();
        Assert.Equal(Application, snapshot.ApplicationDiscriminator);
        Assert.Single(snapshot.KeyIds);
        Assert.Equal(before, Hashes(fixture.Original));
        fixture.CopyRing();
        using var restored = Provider(fixture.Restored, Application, disableKeyGeneration: true);
        DataProtectionRecoveryInventory.RequireCompatible(snapshot, restored.GetRequiredService<DataProtectionRecoveryInventory>().Capture());
        Assert.False(snapshot.DecryptionVerified);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("application")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("empty-id")]
    [InlineData("time")]
    [InlineData("null-ids")]
    public void CompatibilityRejectsIncompleteOrUnknownInventory(string failure)
    {
        var key = Guid.NewGuid();
        var expected = new DataProtectionRecoveryMetadata(1, Application, [key, Guid.NewGuid()], DateTime.UtcNow);
        var actual = expected;
        actual = failure switch
        {
            "empty" => actual with { KeyIds = [] },
            "foreign" => actual with { KeyIds = [Guid.NewGuid()] },
            "application" => actual with { ApplicationDiscriminator = Application + "wrong" },
            "duplicate" => actual with { KeyIds = [key, key] },
            "version" => actual with { FormatVersion = 2 },
            "empty-id" => actual with { KeyIds = [Guid.Empty] },
            "time" => actual with { CapturedAtUtc = default },
            "null-ids" => actual with { KeyIds = null! },
            _ => actual with { KeyIds = [key] },
        };
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.RequireCompatible(expected, actual));
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.RequireCompatible(null, actual));
    }

    [Fact]
    public void SnapshotCompletionIncludesNewKeysButRejectsRemovedKeysOrChangedApplication()
    {
        var before = new DataProtectionRecoveryMetadata(1, Application, [Guid.NewGuid()], DateTime.UtcNow);
        var after = before with { KeyIds = [.. before.KeyIds, Guid.NewGuid()] };
        Assert.Equal(after, DataProtectionRecoveryInventory.CompleteCapture(before, after));
        DataProtectionRecoveryInventory.RequireCompatible(before, after);
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.CompleteCapture(after, before));
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.CompleteCapture(before,
            after with { ApplicationDiscriminator = null }));
        var empty = before with { KeyIds = [] };
        Assert.Equal(empty, DataProtectionRecoveryInventory.CompleteCapture(empty, empty));
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.RequireCompatible(empty, empty));
    }

    [Fact]
    public void RecoveryNeedsRetainedKeysNotOnlyTheNewestKey()
    {
        using var fixture = new Fixture();
        const string purpose = "PayPlanner.ElectronicSignature.Contact.v1";
        string ciphertext;
        Guid originalKey;
        using (var source = Provider(fixture.Original, Application))
        {
            ciphertext = source.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("Old synthetic contact");
            var manager = source.GetRequiredService<IKeyManager>();
            originalKey = manager.GetAllKeys().Single().KeyId;
            manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            Assert.Equal(2, manager.GetAllKeys().Count);
        }
        fixture.CopyRing();
        using (var complete = Provider(fixture.Restored, Application, disableKeyGeneration: true))
            Assert.Equal("Old synthetic contact", complete.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(ciphertext));
        // Retain the generated test key outside the active ring; do not delete any source key.
        File.Move(Path.Combine(fixture.Restored, $"key-{originalKey:D}.xml"), Path.Combine(fixture.Root, "detached-old-key.xml"));
        var before = Hashes(fixture.Restored);
        using (var incomplete = Provider(fixture.Restored, Application, disableKeyGeneration: true))
            Assert.Throws<CryptographicException>(() =>
                incomplete.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(ciphertext));
        Assert.Equal(before, Hashes(fixture.Restored));
        Assert.Equal(2, Directory.GetFiles(fixture.Original, "key-*.xml").Length);
    }

    [Theory]
    [InlineData("PayPlanner.S3Storage.Secrets.v1")]
    [InlineData("PayPlanner.ElectronicSignature.Contact.v1")]
    [InlineData("PayPlanner.MessengerDocumentDelivery.Address.v1")]
    public void CopiedRingReadsProtectedValueAfterOriginalPathBecomesUnavailable(string purpose)
    {
        using var fixture = new Fixture();
        string ciphertext;
        using (var source = Provider(fixture.Original, Application))
            ciphertext = source.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("Тест восстановления — Ω");
        fixture.CopyRing();
        var before = Hashes(fixture.Restored);
        Directory.Move(fixture.Original, Path.Combine(fixture.Root, "detached-original"));
        Assert.False(Directory.Exists(fixture.Original));
        using (var restored = Provider(fixture.Restored, Application, disableKeyGeneration: true))
        {
            var protector = restored.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose);
            Assert.Equal("Тест восстановления — Ω", protector.Unprotect(ciphertext));
            Assert.Throws<CryptographicException>(() =>
                restored.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose + ".wrong").Unprotect(ciphertext));
        }
        Assert.Equal(before, Hashes(fixture.Restored));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-ring")]
    [InlineData("wrong-application")]
    public void MissingOrMismatchedRecoveryDoesNotReturnAnEmptyOrPlaintextValue(string failure)
    {
        using var fixture = new Fixture();
        const string purpose = "PayPlanner.ElectronicSignature.Contact.v1";
        string ciphertext;
        using (var source = Provider(fixture.Original, Application))
            ciphertext = source.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("Synthetic protected contact");
        if (failure == "wrong-application") fixture.CopyRing();
        if (failure == "wrong-ring")
        {
            using var unrelated = Provider(fixture.Restored, Application);
            unrelated.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("Unrelated synthetic value");
        }
        var before = Hashes(fixture.Restored);
        using (var restored = Provider(fixture.Restored, failure == "wrong-application" ? Application + ".other" : Application, disableKeyGeneration: true))
            Assert.Throws<CryptographicException>(() =>
                restored.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(ciphertext));
        Assert.Equal(before, Hashes(fixture.Restored));
    }

    private static ServiceProvider Provider(string path, string application, bool disableKeyGeneration = false)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<DataProtectionRecoveryInventory>();
        var builder = services.AddDataProtection().SetApplicationName(application)
            .PersistKeysToFileSystem(new DirectoryInfo(path));
        if (disableKeyGeneration) builder.DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider();
    }

    private static string[] Hashes(string directory) => Directory.GetFiles(directory)
        .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pp-dp-recovery-" + Guid.NewGuid().ToString("N"));
        public string Original => Path.Combine(Root, "original");
        public string Restored => Path.Combine(Root, "restored");

        public Fixture() { Directory.CreateDirectory(Original); Directory.CreateDirectory(Restored); }

        public void CopyRing()
        {
            var keys = Directory.GetFiles(Original, "key-*.xml");
            Assert.NotEmpty(keys);
            foreach (var path in keys) File.Copy(path, Path.Combine(Restored, Path.GetFileName(path)), overwrite: false);
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(expectedParent, Path.GetDirectoryName(resolved));
            Assert.StartsWith("pp-dp-recovery-", Path.GetFileName(resolved));
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
