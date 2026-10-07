using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task ProvenSnapshotStageReleaseAcknowledgementStillRequiresCopiesAndExactReplay(bool generated, int requiredCopies)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.SourceFullPath);
        var fault = new SnapshotStageCommitFault(3, afterCommit: true);
        await using var db = fixture.CreateIndependentContext(fault);
        var runtime = fixture.CreateManagedRuntime(db, requiredCopies: requiredCopies);
        if (requiredCopies == 2)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Prepare);
            Assert.False(fault.Armed);
            Assert.Empty(await db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.StorageProductBindings.AsNoTracking().ToArrayAsync());
            Assert.Equal(1, await db.StorageReplicas.CountAsync(row => row.Status == StorageReplicaStatuses.Available));
            await runtime.Worker.ProcessBatchAsync("owned-stage-ack-messenger-mirror", default);
        }
        var prepared = await Prepare();
        Assert.False(fault.Armed);
        Assert.True(fault.AfterCommitFaultTriggered);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Equal(requiredCopies, await db.StorageReplicas.CountAsync(row => row.Status == StorageReplicaStatuses.Available));
        Assert.Equal(prepared.Delivery.Id, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        Assert.Equal(prepared.Delivery.Id.ToString(CultureInfo.InvariantCulture), (await db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
        var replay = await Prepare();
        Assert.Equal(prepared.Delivery.Id, replay.Delivery.Id);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Single(await db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourceFullPath));
        var path = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries", prepared.Delivery.SnapshotPath);
        var snapshot = await File.ReadAllBytesAsync(path);
        Assert.Equal(generated ? "%PDF-1.7 recovered release snapshot"u8.ToArray() : sourceBytes, snapshot);
        Assert.Equal(prepared.Delivery.ChecksumSha256, Convert.ToHexString(SHA256.HashData(snapshot)), ignoreCase: true);
        var stages = await db.StorageCapacityReservations.AsNoTracking().Where(row => row.IdempotencyKey.StartsWith("stage:")).ToArrayAsync();
        Assert.NotEmpty(stages);
        Assert.All(stages,
            row => Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status));

        Task<PreparedMessengerDocumentDelivery> Prepare()
        {
            var pdf = "%PDF-1.7 recovered release snapshot"u8.ToArray();
            return generated
                ? runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
                    "invoice.pdf", "application/pdf", new MemoryStream(pdf), pdf.Length, binding.Id, null, default)
                : runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, default);
        }
    }
}

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProvenPdfStageReleaseAcknowledgementStillRequiresCopiesAndExactReplay(int requiredCopies)
    {
        var fault = new StageCommitFault(2, afterCommit: true, checkpointCommits: 1);
        await using var fixture = await Fixture.CreateAsync(interceptors: [fault]);
        var runtime = fixture.CreateManagedRuntime(requiredCopies: requiredCopies);
        var bytes = "%PDF-1.7 recovered signature stage"u8.ToArray();
        using var initial = new ObservedPdfStream(bytes, onEnd: () => fault.Armed = true);
        ElectronicSignatureEnvelope envelope;
        if (requiredCopies == 2)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(fixture.Request(), initial, "invoice.pdf", default));
            Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.ElectronicSignatureSigners.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
            Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync(row => row.Status == StorageReplicaStatuses.Available));
            await runtime.Worker.ProcessBatchAsync("owned-stage-ack-signature-mirror", default);
            envelope = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        }
        else envelope = await runtime.Service.PrepareAsync(fixture.Request(), initial, "invoice.pdf", default);
        Assert.False(fault.Armed);
        Assert.True(fault.AfterCommitFaultTriggered);
        Assert.Equal(requiredCopies, await fixture.Db.StorageReplicas.CountAsync(row => row.Status == StorageReplicaStatuses.Available));
        Assert.Equal(envelope.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        var replay = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        Assert.Equal(envelope.Id, replay.Id);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        var stages = await fixture.Db.StorageCapacityReservations.AsNoTracking().Where(row => row.IdempotencyKey.StartsWith("stage:")).ToArrayAsync();
        Assert.NotEmpty(stages);
        Assert.All(stages,
            row => Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status));
    }

    [Fact]
    public async Task ProvenEvidenceStageReleaseAcknowledgementReturnsCompleteZipWithoutChangingDomain()
    {
        var fault = new StageCommitFault(3, afterCommit: true, countsActivation: true, checkpointCommits: 2);
        await using var fixture = await Fixture.CreateAsync(interceptors: [fault]);
        var bytes = "%PDF-1.7 recovered evidence stage"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        fault.Armed = true;
        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, default);
        Assert.NotNull(package);
        Assert.False(fault.Armed);
        Assert.True(fault.AfterCommitFaultTriggered);
        Assert.Single(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
        Assert.Equal(new FileInfo(package.FullPath).Length, row.Bytes);
        using var archive = ZipFile.OpenRead(package.FullPath);
        await using var original = archive.GetEntry("original/document.pdf")!.Open();
        using var copy = new MemoryStream();
        await original.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }
}
