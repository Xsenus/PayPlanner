using System.Buffers.Binary;
using System.IO.Compression;
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
    public async Task EvidenceZipWritesSequentiallyAndPreservesOriginal(bool capacityChecks)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = "%PDF-1.7 sequential evidence archive"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", CancellationToken.None);
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = capacityChecks;

        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None);
        Assert.NotNull(package);
        var zipBytes = await File.ReadAllBytesAsync(package.FullPath);
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(zipBytes));
        // A data descriptor follows the entry data; its header is never patched
        // by seeking back over bytes which a recovery receipt may have sealed.
        Assert.True((BinaryPrimitives.ReadUInt16LittleEndian(zipBytes.AsSpan(6)) & 0x8) != 0,
            "Evidence ZIP must use a data descriptor instead of rewriting its local header.");
        using var archive = ZipFile.OpenRead(package.FullPath);
        await using var entry = Assert.IsAssignableFrom<ZipArchiveEntry>(archive.GetEntry("original/document.pdf")).Open();
        using var restored = new MemoryStream();
        await entry.CopyToAsync(restored);
        Assert.Equal(original, restored.ToArray());
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        var reservations = await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync();
        if (capacityChecks)
        {
            var row = Assert.Single(reservations);
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(zipBytes.LongLength, row.Bytes);
        }
        else Assert.Empty(reservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceCleanupCannotDeleteOldUnknownOrPendingArchive(bool reserved)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = "%PDF-1.7 preserved evidence cleanup"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", CancellationToken.None);
        IStorageStageCapacityService? stage = null;
        var runtime = CreateEvidenceAdmissionRuntime(fixture, decorate: actual => { stage = actual; return actual; });
        var directory = Path.Combine(fixture.Root, "storage", ".exports");
        Directory.CreateDirectory(directory);
        var oldPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{Guid.NewGuid():N}.zip");
        StorageStageCapacityTicket? ticket = null;
        if (reserved)
        {
            var result = await Assert.IsAssignableFrom<IStorageStageCapacityService>(stage).ReserveCompatibilityAsync(
                $"owned-cleanup-test:{Guid.NewGuid():N}", oldPath, 4096, "esign-current", CancellationToken.None);
            ticket = Assert.IsType<StorageStageCapacityTicket>(result.Ticket);
        }
        var retained = "unconfirmed old evidence bytes"u8.ToArray();
        await File.WriteAllBytesAsync(oldPath, retained);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-25));
        if (ticket is not null) await ticket.CloseWriterAsync();

        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None);
        Assert.NotNull(package);
        Assert.True(File.Exists(oldPath), "Age alone cannot prove ownership or release of an earlier archive.");
        Assert.Equal(retained, await File.ReadAllBytesAsync(oldPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        using var archive = ZipFile.OpenRead(package.FullPath);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        if (ticket is not null)
        {
            var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(item => item.IdempotencyKey == ticket.Key);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Null(row.ProductContextJson);
            Assert.Equal(ticket.ObjectId, row.ObjectId);
            Assert.Equal(ticket.RelativePath, row.LocalLocator);
        }
    }

    [Fact]
    public async Task EvidenceQuotaDenialPrecedesSourceReadAndZipCreation()
    {
        var reader = new EvidenceReader("valid") { Bytes = "%PDF-1.7 admission evidence"u8.ToArray() };
        await using var fixture = await Fixture.CreateAsync();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(reader.Bytes), "invoice.pdf", CancellationToken.None);
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        runtime.Options.CurrentValue.UploadMinimumFreeBytes = long.MaxValue;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.Empty(reader.Bindings);
        Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(reader.Bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task EvidenceZipSealsActualSizeAndPreservesIncompressibleSources()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = "%PDF-1.7 exact evidence"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", CancellationToken.None);
        var bytes = RandomNumberGenerator.GetBytes(256 * 1024);
        var path = Path.Combine(Path.GetDirectoryName(envelope.SnapshotPath)!, "артефакт.bin");
        await File.WriteAllBytesAsync(path, bytes);
        fixture.Db.ElectronicSignatureArtifacts.Add(new ElectronicSignatureArtifact
        {
            EnvelopeId = envelope.Id,
            ArtifactType = "detached-signature",
            FileName = "артефакт.bin",
            StoragePath = path,
            SizeBytes = bytes.Length,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None);
        Assert.NotNull(package);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
        Assert.Equal(new FileInfo(package.FullPath).Length, row.Bytes);
        Assert.NotNull(row.ProductContextJson);
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        using var archive = ZipFile.OpenRead(package.FullPath);
        await using var source = archive.GetEntry("artifacts/001-артефакт.bin")!.Open();
        using var restored = new MemoryStream();
        await source.CopyToAsync(restored);
        Assert.Equal(bytes, restored.ToArray());
        Assert.Equal(3, archive.Entries.Count);
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    private static (ElectronicSignatureService Service, MutableStorageOptions Options) CreateEvidenceAdmissionRuntime(
        Fixture fixture, IProductStorageReader? reader = null, IStorageStageCapacityService? stage = null,
        Func<IStorageStageCapacityService, IStorageStageCapacityService>? decorate = null)
    {
        var environment = new TestEnvironment(fixture.Root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ElectronicSignature:StorageRoot"] = Path.Combine(fixture.Root, "storage"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var options = new MutableStorageOptions(new() { UploadCapacityChecksEnabled = true });
        var registry = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, configuration)]);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(fixture.Root, "keys")));
        stage ??= new StorageStageCapacityService(fixture.Db, registry, options, environment, configuration);
        if (decorate is not null) stage = decorate(stage);
        return (new ElectronicSignatureService(fixture.Db, configuration, protection, environment, reader,
            storageOptions: options, stageCapacity: stage), options);
    }

    [Fact]
    public async Task EvidenceCreateNewConflictPreservesForeignFileAndKeepsCapacityDebt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7 protected conflict source"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        ForeignEvidenceStage? conflict = null;
        var runtime = CreateEvidenceAdmissionRuntime(fixture, decorate: inner => conflict = new ForeignEvidenceStage(inner));
        await Assert.ThrowsAsync<IOException>(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.NotNull(conflict?.Ticket);
        Assert.Equal("foreign export bytes"u8.ToArray(), await File.ReadAllBytesAsync(conflict.Ticket.AbsolutePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Null(row.ProductContextJson);
        Assert.Equal(conflict.Ticket.Key, row.IdempotencyKey);
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
    }

    private sealed class ForeignEvidenceStage(IStorageStageCapacityService inner) : IStorageStageCapacityService
    {
        public StorageStageCapacityTicket? Ticket { get; private set; }
        public Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
            Func<Stream, StorageStageFileReceipt, CancellationToken, Task> write, CancellationToken ct)
            => inner.WriteCheckpointedFileAsync(ticket, write, ct);
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(string identity, string path, long size, CancellationToken ct)
            => inner.ReserveAsync(identity, path, size, ct);
        public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(string identity, string path, long size, string alias, CancellationToken ct)
        {
            var result = await inner.ReserveCompatibilityAsync(identity, path, size, alias, ct);
            Ticket = result.Ticket;
            Assert.NotNull(Ticket);
            await File.WriteAllBytesAsync(path, "foreign export bytes"u8.ToArray(), ct);
            return result;
        }
        public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket, long size, CancellationToken ct)
            => inner.ConfirmPhysicalSizeAsync(ticket, size, ct);
        public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct) => inner.SettlePhysicalAsync(ticket, ct);
        public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct) => inner.ReleaseAsync(ticket, ct);
        public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit, TimeSpan age, CancellationToken ct, string? afterKey = null)
            => inner.ReconcilePendingAsync(apply, limit, age, ct, afterKey);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task EvidenceCapacityPolicyCannotChangeDuringSourceRead(bool enable, bool atEnd)
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7 immutable policy evidence"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var reader = new AdmissionEvidenceReader(bytes, bytes);
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = !enable;
        reader.OnRead = atEnd ? null : () => runtime.Options.CurrentValue.UploadCapacityChecksEnabled = enable;
        reader.OnEnd = atEnd ? () => runtime.Options.CurrentValue.UploadCapacityChecksEnabled = enable : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        var rows = await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync();
        if (enable) Assert.Empty(rows);
        else Assert.Equal(StorageCapacityReservationStatuses.Released, Assert.Single(rows).Status);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task EvidenceCompletionCommitFaultReleasesDebtAndPreservesDomain(int boundary, bool afterCommit)
    {
        // The tiny ZIP adds an initial receipt and one planned tail checkpoint
        // before the original confirm-size / settlement commit boundaries.
        var fault = new StageCommitFault(boundary, afterCommit, countsActivation: true, checkpointCommits: 2);
        var proof = new UnavailableStageReleaseProof(() => afterCommit && boundary == 3 && fault.AfterCommitFaultTriggered);
        await using var fixture = await Fixture.CreateAsync(interceptors: [fault, proof]);
        var bytes = "%PDF-1.7 immutable failure evidence"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.False(fault.Armed);
        Assert.Equal(afterCommit && boundary == 3 ? 1 : 0, proof.Interceptions);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(StorageCapacityReservationStatuses.Released,
            (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("bytes")]
    [InlineData("unavailable")]
    public async Task EvidenceFailedSourceReleasesReservationAndClosesInput(string scenario)
    {
        var reader = new EvidenceReader(scenario) { Bytes = "%PDF-1.7 protected source"u8.ToArray() };
        await using var fixture = await Fixture.CreateAsync();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(reader.Bytes), "invoice.pdf", CancellationToken.None);
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        var error = await Record.ExceptionAsync(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.NotNull(error);
        if (scenario == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.All(reader.Streams, stream => Assert.True(stream.Disposed));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(reader.Bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(StorageCapacityReservationStatuses.Released,
            (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task EvidencePhysicalExtentGuardStopsUnderreportedIncompressibleInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var reader = new AdmissionEvidenceReader(bytes, RandomNumberGenerator.GetBytes(1024 * 1024));
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
        Assert.Contains("зарезервированный", error.Message);
        Assert.NotNull(reader.Stream);
        Assert.InRange(reader.Stream.ReadCalls, 1, 2);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(StorageCapacityReservationStatuses.Released,
            (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class AdmissionEvidenceReader(byte[] metadataBytes, byte[] actualBytes) : IProductStorageReader
    {
        public Action? OnRead { get; set; }
        public Action? OnEnd { get; set; }
        public ObservedPdfStream? Stream { get; private set; }
        public Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
            string contentType, CancellationToken cancellationToken)
        {
            Stream = new ObservedPdfStream(actualBytes, onRead: OnRead, onEnd: OnEnd);
            return Task.FromResult(new ProductStorageReadResult(ProductStorageReadStatus.Available,
                new StorageObjectReadResult(Guid.NewGuid(), 1, contentType, logicalName, metadataBytes.Length,
                    Stream, "mirror", Convert.ToHexString(SHA256.HashData(metadataBytes)))));
        }
    }
}
