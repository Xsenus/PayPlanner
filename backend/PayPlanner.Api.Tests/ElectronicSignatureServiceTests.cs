using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Fact]
    public async Task ManagedOriginalPublishesExactPdfAndReplaysWithoutNewPut()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        byte[] bytes = "%PDF-1.7 managed original"u8.ToArray();
        var envelope = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var binding = await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        Assert.Equal(LegacyStorageSources.ElectronicSignatureOriginals, binding.Source);
        Assert.Equal(envelope.Id, binding.SourceId);
        Assert.Equal(envelope.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (await fixture.Db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
        var replay = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        Assert.Equal(envelope.Id, replay.Id);
        Assert.Equal(1, runtime.Provider.PutCalls);
        File.Delete(envelope.SnapshotPath);
        var package = await runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None);
        Assert.NotNull(package);
        using var archive = ZipFile.OpenRead(package!.FullPath);
        await using var original = archive.GetEntry("original/document.pdf")!.Open();
        using var copy = new MemoryStream();
        await original.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task ManagedOriginalWaitsForRequiredCopiesBeforeSavingEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime(requiredCopies: 2);
        byte[] bytes = "%PDF-1.7 required copies"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureSigners.ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEvents.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        await runtime.Worker.ProcessBatchAsync("owned-esign-mirror", CancellationToken.None);
        var envelope = await PrepareAsync();
        Assert.Equal(envelope.Id, (await fixture.Db.StorageProductBindings.SingleAsync()).SourceId);
        Assert.Equal(2, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, runtime.Provider.PutCalls);
        Task<ElectronicSignatureEnvelope> PrepareAsync() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
    }

    [Fact]
    public async Task ManagedOriginalUnknownResultResumesExactReservationWithoutAnotherPut()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime(loseAcknowledgement: true);
        byte[] bytes = "%PDF-1.7 unknown acknowledgement"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Single(await fixture.Db.StorageCapacityReservations.Where(row => !row.IdempotencyKey.StartsWith("stage:")).ToArrayAsync());
        runtime.Provider.LoseAcknowledgement = false;
        var envelope = await PrepareAsync();
        Assert.Equal(envelope.Id, (await fixture.Db.StorageProductBindings.SingleAsync()).SourceId);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Single(await fixture.Db.StorageObjects.ToArrayAsync());
        Task<ElectronicSignatureEnvelope> PrepareAsync() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("size")]
    [InlineData("checksum")]
    public async Task ManagedOriginalCannotOverwriteUncertainReservedPdf(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime(loseAcknowledgement: true);
        byte[] bytes = "%PDF-1.7 reserved original"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        runtime.Provider.LoseAcknowledgement = false;
        runtime.Provider.RewriteHead = result => mismatch switch
        {
            "unknown" => result with { Status = StorageOperationStatus.Unknown },
            "size" => result with { SizeBytes = result.SizeBytes + 1 },
            _ => result with { Sha256 = new string('0', 64) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
        Task<ElectronicSignatureEnvelope> PrepareAsync() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOriginalRespectsWriterFlagAndReservationPrerequisite(bool capacityEnabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        if (capacityEnabled) runtime.Options.CurrentValue.ProductWriteEnabledClasses.Clear();
        else runtime.Options.CurrentValue.UploadCapacityChecksEnabled = false;
        var preparation = runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream("%PDF-1.7 flags"u8.ToArray()), "invoice.pdf", CancellationToken.None);
        if (capacityEnabled) Assert.True((await preparation).Id > 0);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => preparation);
        Assert.Empty(await fixture.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(0, runtime.Provider.PutCalls);
    }

    [Theory]
    [InlineData("before-save")]
    [InlineData("after-save")]
    [InlineData("before-commit")]
    [InlineData("lost-commit")]
    [InlineData("rollback")]
    public async Task ManagedOriginalFailureBoundariesPreservePdfAndAllowExactRetry(string stage)
    {
        var fault = new ManagedPreparationFault(stage);
        await using var fixture = await Fixture.CreateAsync(interceptors: [fault, new ManagedPreparationSaveFault(fault)]);
        var runtime = fixture.CreateManagedRuntime();
        byte[] bytes = "%PDF-1.7 managed transaction boundary"u8.ToArray();
        fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => PrepareAsync());
        fault.Enabled = false;
        var committed = stage == "lost-commit";
        Assert.Equal(committed ? 1 : 0, await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().CountAsync());
        Assert.Equal(committed ? 2 : 0, await fixture.Db.ElectronicSignatureSigners.AsNoTracking().CountAsync());
        Assert.Equal(committed ? 1 : 0, await fixture.Db.ElectronicSignatureEvents.AsNoTracking().CountAsync());
        Assert.Equal(committed ? 1 : 0, await fixture.Db.StorageProductBindings.AsNoTracking().CountAsync());
        var paths = Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "original.pdf", SearchOption.AllDirectories).ToArray();
        if (committed || stage is "rollback" or "before-commit") Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(paths)));
        else Assert.Empty(paths);
        Assert.Empty(fixture.Db.ChangeTracker.Entries<ElectronicSignatureEnvelope>());
        Assert.Empty(fixture.Db.ChangeTracker.Entries<ElectronicSignatureSigner>());
        Assert.Empty(fixture.Db.ChangeTracker.Entries<ElectronicSignatureEvent>());
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(committed ? 1 : 0, await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().CountAsync());
        var envelope = await PrepareAsync();
        Assert.Equal(envelope.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Task<ElectronicSignatureEnvelope> PrepareAsync() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
    }

    private sealed class ManagedPreparationFault(string stage) : DbTransactionInterceptor
    {
        public string Stage => stage;
        public bool Enabled { get; set; }
        public bool Applies(DbContext? context) => Enabled && context?.ChangeTracker.Entries<ElectronicSignatureEnvelope>().Any() == true;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Applies(eventData.Context) && Stage == "before-commit") throw new IOException("Owned managed signature pre-commit failure");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Applies(eventData.Context) && Stage == "lost-commit") throw new IOException("Owned managed signature lost commit ACK");
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Applies(eventData.Context) && Stage == "rollback") throw new IOException("Owned managed signature unconfirmed rollback");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ManagedPreparationSaveFault(ManagedPreparationFault fault) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (fault.Applies(eventData.Context) && fault.Stage is "before-save" or "rollback") throw new IOException("Owned managed signature save failure");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (fault.Applies(eventData.Context) && fault.Stage == "after-save") throw new IOException("Owned managed signature lost save ACK");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PreparePreservesSnapshotWhenCommitAcknowledgementIsLost()
    {
        var failure = new RejectPreparationCommit();
        await using var fixture = await Fixture.CreateAsync(interceptors: [failure]);
        var bytes = "%PDF-1.7 durable preparation"u8.ToArray();
        failure.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None));
        failure.Enabled = false;
        var saved = await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.SnapshotPath));
        var replay = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(1, await fixture.Db.ElectronicSignatureEnvelopes.CountAsync());
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareRollsBackRegistrationAndCleansSnapshotBeforeCommit(bool afterSave)
    {
        var failure = new RejectPreparationSave(afterSave);
        await using var fixture = await Fixture.CreateAsync(interceptors: [failure]);
        var unrelated = new Client { Name = "Unrelated pending client", IsActive = true };
        failure.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.PrepareAsync(fixture.Request(),
            new MemoryStream("%PDF-1.7 rollback preparation"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        failure.Enabled = false;
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureSigners.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToListAsync());
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        fixture.Db.Clients.Add(unrelated);
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToListAsync());
        Assert.True(await fixture.Db.Clients.AnyAsync(item => item.Id == unrelated.Id));
    }

    private sealed class RejectPreparationCommit : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new IOException("Owned synthetic commit acknowledgement failure.");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparePreservesUncertainSnapshotWhenCommitOrRollbackCannotBeConfirmed(bool rejectRollback)
    {
        var transactionFailure = new RejectPreparationTransaction(rejectRollback);
        var saveFailure = new RejectPreparationSave(false);
        await using var fixture = await Fixture.CreateAsync(interceptors: [transactionFailure, saveFailure]);
        transactionFailure.Enabled = true;
        saveFailure.Enabled = rejectRollback;
        var bytes = "%PDF-1.7 uncertain preparation"u8.ToArray();
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.PrepareAsync(fixture.Request(),
            new MemoryStream(bytes), "invoice.pdf", CancellationToken.None));
        transactionFailure.Enabled = saveFailure.Enabled = false;
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToListAsync());
        var path = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToListAsync());
    }

    private sealed class RejectPreparationTransaction(bool rollback) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !rollback) throw new IOException("Owned synthetic commit failure.");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && rollback) throw new IOException("Owned synthetic rollback failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectPreparationSave(bool afterSave) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !afterSave) throw new IOException("Owned synthetic preparation save failure.");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && afterSave) throw new IOException("Owned synthetic preparation save acknowledgement failure.");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unavailable")]
    [InlineData("deleted")]
    [InlineData("metadata")]
    [InlineData("bytes")]
    [InlineData("size")]
    public async Task EvidencePackageUsesManagedNonSeekableStreamAndFailsClosed(string scenario)
    {
        var reader = new EvidenceReader(scenario);
        await using var fixture = await Fixture.CreateAsync(productStorageReader: reader);
        var bytes = "%PDF-1.7 managed evidence"u8.ToArray();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        reader.Bytes = bytes;
        // A valid legacy file must not bypass a known unavailable or invalid catalog entry.
        if (scenario == "valid") File.Delete(prepared.SnapshotPath);
        if (scenario == "valid")
        {
            var package = await fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None);
            Assert.NotNull(package);
            using var archive = ZipFile.OpenRead(package!.FullPath);
            await using var original = archive.GetEntry("original/document.pdf")!.Open();
            using var copy = new MemoryStream();
            await original.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
            await using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream);
            Assert.Equal(bytes.Length, manifest.RootElement.GetProperty("files")[0].GetProperty("sizeBytes").GetInt64());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        }
        Assert.Equal((LegacyStorageSources.ElectronicSignatureOriginals, (long)prepared.Id, "invoice.pdf", "application/pdf"), reader.Binding);
        Assert.True(reader.Stream is null || reader.Stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidencePackageReadsArtifactBindingAndCleansUpOnLateFailure(bool artifactFailure)
    {
        var reader = new EvidenceReader(artifactFailure ? "artifact-failure" : "valid");
        await using var fixture = await Fixture.CreateAsync(productStorageReader: reader);
        var bytes = "%PDF-1.7 managed evidence"u8.ToArray();
        var prepared = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var artifact = new ElectronicSignatureArtifact
        {
            EnvelopeId = prepared.Id,
            FileName = "signature.bin",
            ContentType = "application/octet-stream",
            ArtifactType = "detached-signature",
            StoragePath = Path.Combine(fixture.Root, "missing.bin"),
            SizeBytes = bytes.Length,
            ChecksumSha256 = prepared.ChecksumSha256
        };
        fixture.Db.ElectronicSignatureArtifacts.Add(artifact);
        await fixture.Db.SaveChangesAsync();
        reader.Bytes = bytes;
        File.Delete(prepared.SnapshotPath);
        if (artifactFailure)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        }
        else
        {
            var package = await fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None);
            using var archive = ZipFile.OpenRead(package!.FullPath);
            await using var entry = archive.GetEntry("artifacts/001-signature.bin")!.Open();
            using var copy = new MemoryStream();
            await entry.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        Assert.Equal(2, reader.Bindings.Count);
        Assert.Equal((LegacyStorageSources.ElectronicSignatures, (long)artifact.Id, "signature.bin", "application/octet-stream"), reader.Binding);
        Assert.All(reader.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task EvidencePackageClosesSourceAndDeletesPartialZipWhenReadingIsCancelled()
    {
        var reader = new EvidenceReader("cancel");
        await using var fixture = await Fixture.CreateAsync(productStorageReader: reader);
        reader.Bytes = "%PDF-1.7 managed evidence"u8.ToArray();
        var prepared = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(reader.Bytes), "invoice.pdf", CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));
        Assert.All(reader.Streams, stream => Assert.True(stream.Disposed));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
    }

    private sealed class EvidenceReader(string scenario) : IProductStorageReader
    {
        public byte[] Bytes { get; set; } = [];
        public NonSeekableEvidenceStream? Stream { get; private set; }
        public (string, long, string, string) Binding { get; private set; }
        public List<(string, long, string, string)> Bindings { get; } = [];
        public List<NonSeekableEvidenceStream> Streams { get; } = [];
        public Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
            string contentType, CancellationToken cancellationToken)
        {
            Binding = (source, fileId, logicalName, contentType);
            Bindings.Add(Binding);
            if (scenario == "artifact-failure" && source == LegacyStorageSources.ElectronicSignatures)
                return Task.FromResult(new ProductStorageReadResult(ProductStorageReadStatus.Unavailable));
            if (scenario is "unavailable" or "deleted")
                return Task.FromResult(new ProductStorageReadResult(scenario == "deleted"
                    ? ProductStorageReadStatus.Deleted : ProductStorageReadStatus.Unavailable));
            Stream = new NonSeekableEvidenceStream(scenario == "bytes" ? "%PDF-1.7 corrupt evidence"u8.ToArray() : Bytes,
                scenario == "cancel", scenario == "oversize" ? 100L * 1024 * 1024 + 1 : 0);
            Streams.Add(Stream);
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes));
            return Task.FromResult(new ProductStorageReadResult(ProductStorageReadStatus.Available,
                new StorageObjectReadResult(Guid.NewGuid(), 1, contentType, logicalName,
                    Bytes.Length + (scenario == "size" ? 1 : 0), Stream, "mirror",
                    scenario == "metadata" ? new string('0', 64) : checksum)));
        }
    }

    [Fact]
    public async Task EvidencePackageLimitsActualBytesEvenWhenManagedSizeIsUnderreported()
    {
        var reader = new EvidenceReader("oversize") { Bytes = "%PDF-1.7"u8.ToArray() };
        await using var fixture = await Fixture.CreateAsync(productStorageReader: reader);
        var prepared = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(reader.Bytes), "invoice.pdf", CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));
        Assert.Contains("превышает 100 МБ", error.Message);
        Assert.NotNull(reader.Stream);
        Assert.Equal(100L * 1024 * 1024 + 1, reader.Stream!.GeneratedBytesRead);
        Assert.True(reader.Stream.Disposed);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
    }

    private sealed class NonSeekableEvidenceStream(byte[] bytes, bool cancel = false, long generatedSize = 0) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public long GeneratedBytesRead { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancel) return ValueTask.FromCanceled<int>(new CancellationToken(true));
            if (generatedSize == 0) return base.ReadAsync(buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, generatedSize - GeneratedBytesRead);
            buffer.Span[..count].Clear();
            GeneratedBytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ControllerListsOnlyClientAndExplicitRepresentativeRecipients()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clientCase = new ClientCase { ClientId = fixture.ClientId, Title = "Дело клиента" };
        var direct = new ClientPortalAccount
        {
            ClientId = fixture.ClientId,
            DisplayName = "Прямой клиент",
            Email = "direct@example.test",
            AccountType = "client"
        };
        var representative = new ClientPortalAccount
        {
            DisplayName = "Явный представитель",
            Email = "representative@example.test",
            AccountType = "representative",
            CaseGrants = [new ClientPortalCaseGrant { ClientCase = clientCase, CanViewCaseSummary = true }]
        };
        var unrelated = new ClientPortalAccount
        {
            DisplayName = "Посторонний аккаунт",
            Email = "other@example.test",
            AccountType = "representative"
        };
        fixture.Db.AddRange(clientCase, direct, representative, unrelated);
        await fixture.Db.SaveChangesAsync();
        var controller = new ElectronicSignaturesController(
            fixture.Db,
            fixture.Service,
            new ConfigurationBuilder().Build());

        var result = await controller.GetRecipients(fixture.ClientId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.Contains("Прямой клиент", json, StringComparison.Ordinal);
        Assert.Contains("Явный представитель", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Посторонний аккаунт", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareCreatesImmutableEncryptedSnapshotAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var bytes = "%PDF-1.7 immutable"u8.ToArray();

        var first = await fixture.Service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var second = await fixture.Service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ElectronicSignatureEnvelopeStatuses.Prepared, first.Status);
        Assert.Equal(2, first.Signers.Count);
        Assert.All(first.Signers, signer => Assert.DoesNotContain("@example.test", signer.ProtectedContact));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEvents.ToListAsync());
    }

    [Fact]
    public async Task DefaultStorageRoot_UsesPersistentApplicationFileStorage()
    {
        await using var fixture = await Fixture.CreateAsync(useExplicitStorageRoot: false);

        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(),
            new MemoryStream("%PDF-1.7 persistent"u8.ToArray()),
            "invoice.pdf",
            CancellationToken.None);

        var expectedRoot = Path.GetFullPath(Path.Combine(fixture.Root, "shared-files", "electronic-signatures"));
        Assert.StartsWith(expectedRoot + Path.DirectorySeparatorChar, prepared.SnapshotPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(prepared.SnapshotPath));
    }

    [Fact]
    public async Task ChangedVersionCreatesNewEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7 immutable"u8.ToArray();
        var first = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        var second = await fixture.Service.PrepareAsync(fixture.Request(version: 2), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await fixture.Db.ElectronicSignatureEnvelopes.CountAsync());
    }

    [Fact]
    public async Task RejectsInvalidPdfAndUnsupportedDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("not a pdf"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            fixture.Request(documentType: "passport"), new MemoryStream("%PDF-1.7"u8.ToArray()), "passport.pdf", CancellationToken.None));

        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
    }

    [Fact]
    public async Task RejectsUnknownProviderAndSignerWithoutRequiredPowerOfAttorney()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        var unknownProvider = request with { Provider = "ManualUpload" };
        var missingMchd = request with
        {
            Signers = [new ElectronicSignatureSignerDraft(
                "Представитель",
                "representative@example.test",
                "representative",
                RequiresMachineReadablePowerOfAttorney: true)]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            unknownProvider, new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(
            missingMchd, new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", CancellationToken.None));
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToListAsync());
    }

    [Fact]
    public void CompletedRequiresVerifiedArtifacts()
    {
        var envelope = new ElectronicSignatureEnvelope { Status = ElectronicSignatureEnvelopeStatuses.Pending };
        Assert.Throws<InvalidOperationException>(() =>
            ElectronicSignatureStateMachine.EnsureTransition(envelope, ElectronicSignatureEnvelopeStatuses.Completed));

        envelope.Artifacts.Add(new ElectronicSignatureArtifact { VerificationStatus = "verified" });
        ElectronicSignatureStateMachine.EnsureTransition(envelope, ElectronicSignatureEnvelopeStatuses.Completed);
        Assert.False(ElectronicSignatureStateMachine.CanTransition(
            ElectronicSignatureEnvelopeStatuses.Completed,
            ElectronicSignatureEnvelopeStatuses.Pending));
    }

    [Fact]
    public async Task DisabledProviderNeverSendsDocument()
    {
        var provider = new DisabledElectronicSignatureProvider("KonturDiadoc");
        var result = await provider.CreateEnvelopeAsync(
            new ElectronicSignatureEnvelope(), new MemoryStream("%PDF-"u8.ToArray()), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("не подключён", result.Error);
    }

    [Fact]
    public async Task PreparedEnvelopeCancellationIsIdempotentAndPreservesSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "%PDF-1.7 immutable"u8.ToArray();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);

        var first = await fixture.Service.CancelPreparedAsync(prepared.PublicId, fixture.UserId, "Документ заменён", CancellationToken.None);
        var second = await fixture.Service.CancelPreparedAsync(prepared.PublicId, fixture.UserId, null, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(ElectronicSignatureEnvelopeStatuses.Cancelled, second!.Status);
        Assert.NotNull(second.CancelledAt);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(second.SnapshotPath));
        var events = await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Single(events, item => item.EventType == "cancelled-by-user");
        Assert.Contains("Документ заменён", events.Single(item => item.EventType == "cancelled-by-user").Message);
    }

    [Fact]
    public async Task SentEnvelopeCannotBeCancelledLocally()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", CancellationToken.None);
        prepared.Status = ElectronicSignatureEnvelopeStatuses.Pending;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CancelPreparedAsync(prepared.PublicId, fixture.UserId, null, CancellationToken.None));

        Assert.Contains("ещё не отправленную", error.Message);
    }

    [Fact]
    public async Task EvidencePackageIsSelfContainedAndVerifiesSnapshotChecksum()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("%PDF-1.7 evidence"u8.ToArray()), "invoice.pdf", CancellationToken.None);

        var package = await fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None);

        Assert.NotNull(package);
        var restoredPath = Path.Combine(fixture.Root, "restored-evidence.zip");
        File.Copy(package!.FullPath, restoredPath);
        using var archive = ZipFile.OpenRead(restoredPath);
        Assert.NotNull(archive.GetEntry("original/document.pdf"));
        var manifestEntry = Assert.Single(archive.Entries, item => item.FullName == "manifest.json");
        await using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.Equal(prepared.PublicId, manifest.RootElement.GetProperty("envelope").GetProperty("publicId").GetGuid());
        Assert.Equal(prepared.ChecksumSha256, manifest.RootElement.GetProperty("files")[0].GetProperty("checksumSha256").GetString());
    }

    [Fact]
    public async Task EvidencePackageRejectsTamperedSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("%PDF-1.7 evidence"u8.ToArray()), "invoice.pdf", CancellationToken.None);
        await File.AppendAllTextAsync(prepared.SnapshotPath, "tampered");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));

        Assert.Contains("Контрольная сумма", error.Message);
    }

    [Fact]
    public async Task EvidencePackageRejectsArtifactOutsideProtectedStorage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var prepared = await fixture.Service.PrepareAsync(
            fixture.Request(), new MemoryStream("%PDF-1.7 evidence"u8.ToArray()), "invoice.pdf", CancellationToken.None);
        var outsidePath = Path.Combine(fixture.Root, "outside-signature.bin");
        await File.WriteAllTextAsync(outsidePath, "signature");
        fixture.Db.ElectronicSignatureArtifacts.Add(new ElectronicSignatureArtifact
        {
            EnvelopeId = prepared.Id,
            ArtifactType = "detached-signature",
            StoragePath = outsidePath,
            FileName = "signature.bin",
            SizeBytes = new FileInfo(outsidePath).Length,
            ChecksumSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("signature"u8.ToArray())).ToLowerInvariant()
        });
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateEvidencePackageAsync(prepared.PublicId, CancellationToken.None));

        Assert.Contains("вне защищённого хранилища", error.Message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _root;
        public PaymentContext Db { get; }
        public ElectronicSignatureService Service { get; }
        public int UserId { get; }
        public int ClientId { get; }
        public string Root => _root;

        private Fixture(SqliteConnection connection, string root, PaymentContext db, ElectronicSignatureService service, int userId, int clientId)
        {
            _connection = connection;
            _root = root;
            Db = db;
            Service = service;
            UserId = userId;
            ClientId = clientId;
        }

        public ElectronicSignaturePreparation Request(int version = 1, string documentType = "invoice") => new(
            documentType,
            101,
            version,
            ClientId,
            ElectronicSignatureTypes.Qualified,
            ElectronicSignatureSigningOrders.Sequential,
            "KonturDiadoc",
            10,
            true,
            UserId,
            [
                new("Клиент", "client@example.test"),
                new("Представитель", "representative@example.test", "representative", RequiresMachineReadablePowerOfAttorney: true, PowerOfAttorneyReference: "МЧД-1")
            ]);

        public static async Task<Fixture> CreateAsync(bool useExplicitStorageRoot = true, IProductStorageReader? productStorageReader = null,
            IInterceptor[]? interceptors = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection)
                .AddInterceptors(interceptors ?? []).Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "admin", Description = "Администратор" };
            var user = new User
            {
                Email = "admin@example.test",
                FullName = "Администратор",
                PasswordHash = "hash",
                Role = role,
                IsActive = true,
                IsApproved = true
            };
            var client = new Client { Name = "Клиент", IsActive = true };
            db.AddRange(user, client);
            await db.SaveChangesAsync();

            var root = Path.Combine(Path.GetTempPath(), $"payplanner-esign-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var configurationValues = new Dictionary<string, string?>
            {
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["FileStorage:RootPath"] = Path.Combine(root, "shared-files")
            };
            if (useExplicitStorageRoot)
            {
                configurationValues["ElectronicSignature:StorageRoot"] = Path.Combine(root, "storage");
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new ElectronicSignatureService(db, configuration, protection, new TestEnvironment(root), productStorageReader);
            return new Fixture(connection, root, db, service, user.Id, client.Id);
        }

        public (ElectronicSignatureService Service, CountingProvider Provider, StorageOperationProcessor Worker, MutableStorageOptions Options)
            CreateManagedRuntime(int requiredCopies = 1, bool loseAcknowledgement = false)
        {
            var environment = new TestEnvironment(_root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["ElectronicSignature:StorageRoot"] = Path.Combine(_root, "storage"),
                ["FileStorage:RootPath"] = Path.Combine(_root, "shared-files"),
                ["StorageOrchestration:LocalRoot"] = Path.Combine(_root, "objects")
            }).Build();
            var provider = new CountingProvider(new LocalStorageProvider(environment, configuration)) { LoseAcknowledgement = loseAcknowledgement };
            var mirrorConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["StorageOrchestration:LocalRoot"] = Path.Combine(_root, "mirror") }).Build();
            var mirror = new CountingProvider(new LocalStorageProvider(environment, mirrorConfiguration), "mirror");
            var registry = new StorageProviderRegistry(requiredCopies == 1 ? [provider, new LegacyLocalStorageProvider(environment, configuration)] : [provider, mirror, new LegacyLocalStorageProvider(environment, configuration)]);
            var options = new MutableStorageOptions(new()
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                WorkerEnabled = true,
                ProductWriteEnabledClasses = ["esign-original"],
                ProductReadEnabledClasses = ["esign-original"],
                ProviderConnections = new() { ["mirror"] = "owned" },
                DefaultPolicy = new()
                {
                    RequiredCopies = requiredCopies,
                    WriteProviders = requiredCopies == 1 ? ["local"] : ["local", "mirror"],
                    ReadProviders = requiredCopies == 1 ? ["local"] : ["local", "mirror"]
                }
            });
            var policy = new StoragePolicyService(options, registry);
            var storage = new StorageObjectService(Db, policy, registry, options);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys")));
            var service = new ElectronicSignatureService(Db, configuration, protection, environment,
                new ProductStorageReader(Db, storage, options), managedStorage: storage,
                storagePublisher: new ProductStorageBindingService(Db, policy), storageOptions: options, stageCapacity: new StorageStageCapacityService(Db, registry, options, environment, configuration));
            return (service, provider, new(Db, registry, options, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance), options);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class MutableStorageOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class CountingProvider(IStorageProvider inner, string? key = null) : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => key ?? inner.Key;
        public StorageCapacityDomain CapacityDomain => inner.CapacityDomain;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public bool LoseAcknowledgement { get; set; }
        public int PutCalls { get; private set; }
        public Func<StorageObjectMetadata, StorageObjectMetadata>? RewriteHead { get; set; }
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, key is null ? connectionId : null, minimumFreeBytes, ct);
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            PutCalls++;
            var result = await inner.PutAsync(request, ct);
            return LoseAcknowledgement && result.Status == StorageOperationStatus.Succeeded
                ? result with { Status = StorageOperationStatus.Unknown, Error = "Owned lost signature original ACK" } : result;
        }
        public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
        {
            var result = await inner.HeadAsync(locator, ct);
            return RewriteHead?.Invoke(result) ?? result;
        }
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
