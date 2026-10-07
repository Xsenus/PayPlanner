using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SignaturePdfLargeInputFailureRetainsConfirmedPrefixAndFullDebt(bool seekable, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        var bytes = RandomNumberGenerator.GetBytes(StorageStageFileReceipt.MaximumBlockBytes * 2);
        "%PDF-1.7"u8.CopyTo(bytes);
        var sourceError = cancel ? (Exception)new OperationCanceledException("Owned PDF input cancellation after bytes.")
            : new IOException("Owned PDF input failure after bytes.");
        using var input = new ObservedPdfStream(bytes, seekable, onEnd: () => throw sourceError);
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", default));
        Assert.Same(sourceError, error);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Null(row.ProductContextJson);
        Assert.Equal(seekable ? bytes.LongLength : 25L * 1024 * 1024, row.Bytes);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
        Assert.Equal(2, receipt!.Sequence);
        Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, receipt.PreviousBytes);
        Assert.Equal(bytes.LongLength, receipt.PlannedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), receipt.PlannedSha256);
        var path = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        await using (var file = File.OpenRead(path))
            Assert.Equal(receipt.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignaturePdfConfirmsEmptyCheckpointBeforeInputReadAndPreservesFinalReceipt(bool seekable)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        var bytes = "%PDF-1.7 owned checkpoint source"u8.ToArray();
        StorageStageFileReceipt? initial = null;
        var observed = false;
        using var input = new ObservedPdfStream(bytes, seekable, onRead: () =>
        {
            if (observed) return;
            var row = fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync().GetAwaiter().GetResult();
            Assert.False(row.StageWriterAdmissionPending);
            Assert.NotNull(row.StageRootIdentity);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out initial),
                "Signature PDF input must not be read before the original empty file checkpoint is committed.");
            Assert.Equal(0, initial!.Sequence);
            Assert.Equal(0, initial.PlannedBytes);
            Assert.Equal(StorageStageFileReceipt.EmptySha256, initial.PlannedSha256);
            observed = true;
        });
        var envelope = await runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", default);
        Assert.True(observed);
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
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        await using (var file = File.OpenRead(envelope.SnapshotPath))
            Assert.Equal(completed.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Equal(3, input.ReadCalls);
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
    }
}
