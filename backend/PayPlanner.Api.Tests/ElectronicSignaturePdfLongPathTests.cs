using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignaturePreparationPublishesExactPdfUnderLongStorageRoot(bool capacityEnabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var root = Path.Combine(fixture.Root, "long-storage");
        while (root.Length < 310) root = Path.Combine(root, "owned-long-signature-directory");
        Directory.CreateDirectory(root);
        var environment = new TestEnvironment(fixture.Root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ElectronicSignature:StorageRoot"] = root,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var options = new MutableStorageOptions(new() { UploadCapacityChecksEnabled = capacityEnabled });
        var registry = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, configuration)]);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(fixture.Root, "keys")));
        var stages = new StorageStageCapacityService(fixture.Db, registry, options, environment, configuration);
        var service = new ElectronicSignatureService(fixture.Db, configuration, protection, environment,
            storageOptions: options, stageCapacity: stages);
        var bytes = "%PDF-1.7 owned long-path preparation"u8.ToArray();
        var envelope = await service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        Assert.True(envelope.SnapshotPath.Length > 300);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, envelope.SnapshotPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), envelope.ChecksumSha256);
        Assert.Equal(envelope.Id, (await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        var reservations = await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync();
        if (capacityEnabled)
        {
            var row = Assert.Single(reservations);
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
            Assert.Equal(bytes.LongLength, receipt!.PlannedBytes);
            Assert.Equal(envelope.ChecksumSha256, receipt.PlannedSha256);
            using var file = File.OpenRead(envelope.SnapshotPath);
            Assert.Equal(receipt.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        }
        else Assert.Empty(reservations);
    }
}
