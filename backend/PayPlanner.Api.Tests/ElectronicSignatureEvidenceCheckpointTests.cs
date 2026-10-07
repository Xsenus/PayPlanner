using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceLargeSourceFailureRetainsExactCheckpointPrefixAndDebt(bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var original = RandomNumberGenerator.GetBytes(StorageStageFileReceipt.MaximumBlockBytes * 2);
        "%PDF-1.7"u8.CopyTo(original);
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", default);
        var reader = new AdmissionEvidenceReader(original, original)
        {
            OnEnd = () =>
            {
                if (cancel) throw new OperationCanceledException("Owned evidence source cancellation after bytes.");
                throw new IOException("Owned evidence source failure after bytes.");
            }
        };
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        var error = await Record.ExceptionAsync(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, default));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.NotNull(reader.Stream);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.Stream.ReadAsync(new byte[1]).AsTask());
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Null(row.ProductContextJson);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var checkpoint));
        Assert.Equal(2, checkpoint!.Sequence);
        Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, checkpoint.PreviousBytes);
        Assert.Equal(original.LongLength, checkpoint.PlannedBytes);
        Assert.True(row.Bytes > checkpoint.PlannedBytes);
        var path = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        var retained = await File.ReadAllBytesAsync(path);
        Assert.Equal(checkpoint.PlannedBytes, retained.LongLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant(), checkpoint.PlannedSha256);
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(checkpoint.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task EvidenceZipConfirmsEmptyCheckpointBeforeSourceReadAndPreservesFinalReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var original = "%PDF-1.7 checkpoint evidence archive"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", default);
        StorageStageFileReceipt? initial = null;
        var observed = false;
        var reader = new AdmissionEvidenceReader(original, original)
        {
            OnRead = () =>
            {
                var pending = fixture.Db.StorageCapacityReservations.AsNoTracking().Single();
                Assert.Equal(StorageCapacityReservationStatuses.Pending, pending.Status);
                Assert.False(pending.StageWriterAdmissionPending);
                Assert.True(StorageStageFileReceipt.TryParse(pending.StageFileReceiptJson, out initial),
                    "Evidence source must not be read before the original empty file checkpoint is committed.");
                Assert.Equal(0, initial!.Sequence);
                Assert.Equal(0, initial.PlannedBytes);
                Assert.Equal(StorageStageFileReceipt.EmptySha256, initial.PlannedSha256);
                Assert.NotNull(pending.StageRootIdentity);
                observed = true;
            }
        };
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, default);
        Assert.NotNull(package);
        Assert.True(observed);
        var bytes = await File.ReadAllBytesAsync(package.FullPath);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var completed));
        Assert.Equal(initial!.FileIdentity, completed!.FileIdentity);
        Assert.Equal(initial.AdmissionBindingHash, completed.AdmissionBindingHash);
        Assert.Equal(1, completed.Sequence);
        Assert.Equal(bytes.LongLength, completed.PlannedBytes);
        Assert.True(completed.UpperBound >= completed.PlannedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), completed.PlannedSha256);
        Assert.Equal(bytes.LongLength, row.Bytes);
        Assert.NotNull(row.ProductContextJson);
        using var archive = ZipFile.OpenRead(package.FullPath);
        await using var entry = archive.GetEntry("original/document.pdf")!.Open();
        using var restored = new MemoryStream();
        await entry.CopyToAsync(restored);
        Assert.Equal(original, restored.ToArray());
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }
}
