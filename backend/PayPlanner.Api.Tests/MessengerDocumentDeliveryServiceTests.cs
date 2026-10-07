using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using System.Data.Common;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedPreparationPublishesExactSnapshotAndReplaysWithoutNewObject(bool generated)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        var prepared = await PrepareAsync(runtime.Service);
        var pointer = await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        Assert.Equal(LegacyStorageSources.MessengerSnapshots, pointer.Source);
        Assert.Equal(prepared.Delivery.Id, pointer.SourceId);
        Assert.Equal(prepared.Delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (await fixture.Db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
        Assert.Equal(1, runtime.Provider.PutCalls);
        var replay = await PrepareAsync(runtime.Service);
        Assert.Equal(prepared.Delivery.Id, replay.Delivery.Id);
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Single(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        // The managed endpoint remains readable after loss of the compatibility copy.
        File.Delete(Path.Combine(fixture.Configuration["FileStorage:RootPath"]!,
            "messenger-document-deliveries", prepared.Delivery.SnapshotPath));
        var download = await runtime.Service.ConsumeDownloadAsync(replay.Delivery.PublicId, replay.Token!, CancellationToken.None);
        Assert.NotNull(download);
        using var content = new MemoryStream();
        await download!.Content!.CopyToAsync(content);
        await download.Content.DisposeAsync();
        Assert.Equal(prepared.Delivery.SizeBytes, content.Length);
        Assert.Equal(prepared.Delivery.ChecksumSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.ToArray())), ignoreCase: true);

        Task<PreparedMessengerDocumentDelivery> PrepareAsync(MessengerDocumentDeliveryService service)
        {
            byte[] pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 owned managed snapshot");
            return generated ? service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1,
                fixture.ClientId, "invoice.pdf", "application/pdf", new MemoryStream(pdf), pdf.Length,
                binding.Id, null, CancellationToken.None)
                : service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ManagedUnknownResultCannotPublishAndRetryUsesVerifiedReceiptWithoutAnotherPut()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db, loseAcknowledgement: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.StorageCapacityReservations.AsNoTracking().Where(row => !row.IdempotencyKey.StartsWith("stage:")).ToArrayAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
        runtime.Provider.LoseAcknowledgement = false;
        await runtime.Worker.ProcessBatchAsync("owned-messenger-recovery", CancellationToken.None);
        var prepared = await PrepareAsync();
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Equal(prepared.Delivery.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        Assert.Single(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());

        Task<PreparedMessengerDocumentDelivery> PrepareAsync() => runtime.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
    }

    [Fact]
    public async Task ManagedDomainRollbackLeavesUnpublishedIntentThatCanBeRetried()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        await using (var failing = fixture.CreateIndependentContext(new RejectDeliverySave()))
        {
            var runtime = fixture.CreateManagedRuntime(failing);
            await Assert.ThrowsAsync<IOException>(() => runtime.Service.PrepareCaseDocumentAsync(
                fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));
            await failing.SaveChangesAsync();
        }
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        var pending = await fixture.Db.StorageObjects.AsNoTracking().SingleAsync();
        Assert.StartsWith("upload:messenger:", pending.OwnerId, StringComparison.Ordinal);
        var resumed = fixture.CreateManagedRuntime(fixture.Db);
        var prepared = await resumed.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        Assert.Equal(0, resumed.Provider.PutCalls);
        Assert.Equal(pending.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).StorageObjectId);
        Assert.Equal(prepared.Delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (await fixture.Db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task DisabledManagedSnapshotWriterKeepsLegacyPathWithoutCatalogWrites()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        runtime.Options.CurrentValue.ProductWriteEnabledClasses.Clear();
        var prepared = await runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        Assert.True(prepared.Delivery.Id > 0);
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(0, runtime.Provider.PutCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedPreparationPreservesSnapshotWhenCommitOrRollbackCannotBeConfirmed(bool rollbackFailure)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var fault = new DeliveryTransactionFault(rollbackFailure);
        var interceptors = rollbackFailure ? new IInterceptor[] { new RejectDeliverySave(), fault } : [fault];
        await using (var failing = fixture.CreateIndependentContext(interceptors))
        {
            var runtime = fixture.CreateManagedRuntime(failing);
            await Assert.ThrowsAsync<IOException>(() => runtime.Service.PrepareCaseDocumentAsync(
                fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));
            if (rollbackFailure) await failing.SaveChangesAsync();
        }
        var snapshots = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
        var path = Assert.Single(Directory.EnumerateFiles(snapshots, "*.snapshot", SearchOption.AllDirectories));
        Assert.Equal(await File.ReadAllBytesAsync(fixture.SourceFullPath), await File.ReadAllBytesAsync(path));
        if (rollbackFailure)
        {
            Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        }
        else
        {
            var original = await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().SingleAsync();
            var runtime = fixture.CreateManagedRuntime(fixture.Db);
            var replay = await runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
            Assert.Equal(original.Id, replay.Delivery.Id);
            Assert.Equal(0, runtime.Provider.PutCalls);
            Assert.Equal(original.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        }
    }

    private sealed class DeliveryTransactionFault(bool rollbackFailure) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!rollbackFailure && eventData.Context?.ChangeTracker.Entries<MessengerDocumentDelivery>().Any() == true)
                throw new IOException("Owned lost domain commit acknowledgement");
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (rollbackFailure && eventData.Context?.ChangeTracker.Entries<MessengerDocumentDelivery>().Any() == true)
                throw new IOException("Owned unconfirmed rollback");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("checksum")]
    [InlineData("size")]
    public async Task ManagedRetryCannotOverwriteUncertainOrMismatchedReservedFile(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db, loseAcknowledgement: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        runtime.Provider.LoseAcknowledgement = false;
        runtime.Provider.RewriteHead = result => mismatch switch
        {
            "unknown" => result with { Status = StorageOperationStatus.Unknown },
            "checksum" => result with { Sha256 = new string('0', 64) },
            _ => result with { SizeBytes = result.SizeBytes + 1 }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.StorageCapacityReservations.AsNoTracking().Where(row => !row.IdempotencyKey.StartsWith("stage:")).ToArrayAsync());

        Task<PreparedMessengerDocumentDelivery> PrepareAsync() => runtime.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
    }

    [Fact]
    public async Task ManagedPreparationRequiresDurableReservationBeforeProviderIo()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSnapshotRegistrationDoesNotLeaveUnlinkedFile(bool generated)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        await using var db = fixture.CreateIndependentContext(new RejectDeliverySave());
        var service = fixture.CreateIndependentService(db);
        if (generated)
        {
            var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 generated");
            await Assert.ThrowsAsync<IOException>(() => service.PrepareGeneratedDocumentAsync(
                fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf",
                new MemoryStream(pdf), pdf.Length, binding.Id, null, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => service.PrepareCaseDocumentAsync(
                fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));
        }

        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        var snapshotRoot = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
        Assert.Empty(Directory.Exists(snapshotRoot)
            ? Directory.EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task ManagedPreparationWaitsForAllRequiredCopiesBeforePublishingLink()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db, requiredCopies: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync());
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, await fixture.Db.StorageReplicas.AsNoTracking().CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        await runtime.Worker.ProcessBatchAsync("owned-messenger-mirror", CancellationToken.None);
        var prepared = await PrepareAsync();
        Assert.Equal(prepared.Delivery.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
        Assert.Equal(2, await fixture.Db.StorageReplicas.AsNoTracking().CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, runtime.Provider.PutCalls);

        Task<PreparedMessengerDocumentDelivery> PrepareAsync() => runtime.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
    }

    private sealed class RejectDeliverySave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<MessengerDocumentDelivery>()
                .Any(entry => entry.State == EntityState.Added) == true)
                throw new IOException("Owned synthetic delivery registration failure.");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("available")]
    [InlineData("unavailable")]
    [InlineData("wrongChecksum")]
    [InlineData("wrongSize")]
    [InlineData("expiresDuringRead")]
    [InlineData("cancelledDuringRead")]
    [InlineData("invalidToken")]
    public async Task ManagedDownload_VerifiesSnapshotBeforeConsumingLink(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(fixture.ExpectedContent);
        var reader = new ManagedSnapshotReader(bytes, fixture.ExpectedChecksum)
        {
            Unavailable = scenario == "unavailable",
            WrongChecksum = scenario == "wrongChecksum",
            WrongSize = scenario == "wrongSize",
        };
        if (scenario == "expiresDuringRead")
            reader.BeforeRead = async () =>
            {
                await fixture.Db.MessengerDocumentDeliveries.Where(item => item.Id == prepared.Delivery.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LinkExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            };
        if (scenario == "cancelledDuringRead")
            reader.BeforeRead = async () =>
            {
                await fixture.Db.MessengerDocumentDeliveries.Where(item => item.Id == prepared.Delivery.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CancelledAt, DateTime.UtcNow));
            };
        var service = fixture.CreateIndependentService(fixture.Db, reader);
        var controller = new PublicMessengerDocumentDeliveriesController(service, fixture.Db, fixture.Configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = await controller.DownloadSecurely(prepared.Delivery.PublicId,
            new MessengerDocumentDownloadRequest { Token = scenario == "invalidToken" ? "wrong-token" : prepared.Token! }, CancellationToken.None);
        var stored = await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().SingleAsync(item => item.Id == prepared.Delivery.Id);
        if (scenario == "available")
        {
            var download = Assert.IsType<FileStreamResult>(result);
            Assert.False(download.EnableRangeProcessing);
            Assert.Equal("no-store, private", controller.Response.Headers.CacheControl);
            Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
            Assert.Equal("noindex, nofollow, noarchive", controller.Response.Headers["X-Robots-Tag"]);
            await using var stream = download.FileStream;
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(bytes, memory.ToArray());
            Assert.Equal(1, stored.DownloadCount);
            Assert.Null(await service.ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None));
            Assert.Equal(1, reader.Calls);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(0, stored.DownloadCount);
            Assert.Null(stored.ReadAt);
            Assert.Equal(scenario == "invalidToken" ? 0 : 1, reader.Calls);
            Assert.All(reader.Streams, stream => Assert.True(stream.Disposed));
            if (scenario is "unavailable" or "wrongChecksum" or "wrongSize")
            {
                reader.Unavailable = reader.WrongChecksum = reader.WrongSize = false;
                await using var recovered = await service.ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None);
                Assert.NotNull(recovered);
                Assert.NotNull(recovered.Content);
            }
        }
    }

    [Fact]
    public async Task ManagedParallelDownload_ConsumesOnceAndClosesLosingStream()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        await using var firstDb = fixture.CreateIndependentContext();
        await using var secondDb = fixture.CreateIndependentContext();
        var bytes = System.Text.Encoding.UTF8.GetBytes(fixture.ExpectedContent);
        var bothReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsStarted = 0;
        var firstReader = new ManagedSnapshotReader(bytes, fixture.ExpectedChecksum) { BeforeRead = WaitForBothReadersAsync };
        var secondReader = new ManagedSnapshotReader(bytes, fixture.ExpectedChecksum) { BeforeRead = WaitForBothReadersAsync };
        var results = await Task.WhenAll(
            fixture.CreateIndependentService(firstDb, firstReader).ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None),
            fixture.CreateIndependentService(secondDb, secondReader).ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None));
        var winner = Assert.Single(results, item => item is not null)!;
        Assert.Equal(1, firstReader.Calls);
        Assert.Equal(1, secondReader.Calls);
        Assert.Single(firstReader.Streams.Concat(secondReader.Streams), stream => stream.Disposed);
        await winner.DisposeAsync();
        Assert.All(firstReader.Streams.Concat(secondReader.Streams), stream => Assert.True(stream.Disposed));
        await using var verification = fixture.CreateIndependentContext();
        Assert.Equal(1, (await verification.MessengerDocumentDeliveries.AsNoTracking().SingleAsync()).DownloadCount);
        async Task WaitForBothReadersAsync()
        {
            if (Interlocked.Increment(ref readsStarted) == 2) bothReaders.TrySetResult();
            await bothReaders.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class TrackedReadStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ManagedSnapshotReader(byte[] bytes, string checksum) : IProductStorageReader
    {
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }
        public bool WrongChecksum { get; set; }
        public bool WrongSize { get; set; }
        public Func<Task>? BeforeRead { get; set; }
        public List<TrackedReadStream> Streams { get; } = [];
        public async Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName, string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(LegacyStorageSources.MessengerSnapshots, source);
            if (BeforeRead is not null) await BeforeRead();
            if (Unavailable) return new(ProductStorageReadStatus.Unavailable);
            var stream = new TrackedReadStream(bytes);
            Streams.Add(stream);
            return new(ProductStorageReadStatus.Available, new(LegacyStorageSources.GetObjectId(source, fileId), 1,
                contentType, logicalName, WrongSize ? bytes.Length + 1 : bytes.Length, stream, "mirror",
                WrongChecksum ? new string('0', 64) : checksum.ToLowerInvariant()));
        }
    }
    [Fact]
    public void DirectAttachmentsRemainDisabledWithoutAntivirusAndProviderLimit()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessengerDocumentDelivery:DirectAttachments:Enabled"] = "true",
            ["MessengerDocumentDelivery:DirectAttachments:AllowedMimeTypes:0"] = "application/pdf",
            ["MessengerDocumentDelivery:DirectAttachments:MaxBytes"] = "0",
            ["MessengerDocumentDelivery:DirectAttachments:Antivirus:Enabled"] = "false"
        }).Build();

        var status = MessengerDirectAttachmentPolicy.GetStatus(configuration);

        Assert.True(status.Requested);
        Assert.False(status.Enabled);
        Assert.Equal("protected_link", status.DeliveryMode);
        Assert.Contains("antivirus", status.DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectAttachmentsRequireAllSafetyGates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessengerDocumentDelivery:DirectAttachments:Enabled"] = "true",
            ["MessengerDocumentDelivery:DirectAttachments:AllowedMimeTypes:0"] = "APPLICATION/PDF",
            ["MessengerDocumentDelivery:DirectAttachments:AllowedMimeTypes:1"] = "application/pdf",
            ["MessengerDocumentDelivery:DirectAttachments:MaxBytes"] = "1048576",
            ["MessengerDocumentDelivery:DirectAttachments:Antivirus:Enabled"] = "true"
        }).Build();

        var status = MessengerDirectAttachmentPolicy.GetStatus(configuration);

        Assert.True(status.Enabled);
        Assert.Equal("direct_attachment", status.DeliveryMode);
        Assert.Equal(["application/pdf"], status.AllowedMimeTypes);
        Assert.Null(status.DisabledReason);
    }

    [Fact]
    public async Task MaxProviderQueuesOnlyProtectedLinkForVerifiedPortalRecipient()
    {
        var outbox = new RecordingMaxOutbox();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessengerDocumentDelivery:Providers:Max:Enabled"] = "true"
        }).Build();
        var provider = new MaxMessengerDocumentProvider(outbox, configuration);

        var result = await provider.SendAsync(new MessengerDocumentProviderRequest(
            17,
            "protected-address-is-not-used",
            "Произвольный текст не должен попасть в MAX",
            "https://crm.example.test/public/document/id#token=secret",
            "delivery-key",
            42), CancellationToken.None);

        Assert.True(result.Accepted);
        var request = Assert.Single(outbox.Requests);
        Assert.Null(request.RecipientUserId);
        Assert.Equal(42, request.RecipientPortalAccountId);
        Assert.Equal(MaxMessengerScenarios.Documents, request.Scenario);
        Assert.Equal("Открыть документ", request.ActionText);
        Assert.StartsWith("https://", request.ActionUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("Произвольный", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledFeatureRejectsRecipientBinding()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateVerifiedBindingAsync(
                fixture.UserId, fixture.ClientId, null, "telegram", "telegram-bot",
                "Клиент", "123456", "admin-confirmed", CancellationToken.None));

        Assert.Contains("отключена", error.Message);
        Assert.Empty(await fixture.Db.MessengerRecipientBindings.ToListAsync());
    }

    [Fact]
    public async Task PrepareCreatesImmutableSnapshotAndOneTimeDownload()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();

        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Документ по делу", CancellationToken.None);

        Assert.NotNull(prepared.Token);
        Assert.Equal(MessengerDocumentDeliveryStatuses.Prepared, prepared.Delivery.Status);
        Assert.Equal(fixture.ExpectedChecksum, prepared.Delivery.ChecksumSha256);
        Assert.NotEqual(fixture.SourcePath, prepared.Delivery.SnapshotPath);

        await File.WriteAllTextAsync(fixture.SourceFullPath, "изменённый исходник");
        var download = await fixture.Service.ConsumeDownloadAsync(
            prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None);
        Assert.NotNull(download);
        Assert.Equal(fixture.ExpectedContent, await File.ReadAllTextAsync(download.FullPath));
        Assert.Null(await fixture.Service.ConsumeDownloadAsync(
            prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None));
    }

    [Fact]
    public async Task ParallelDownloadConsumesOneTimeLinkExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var barrier = new ParallelSelectBarrier();
        await using var firstDb = fixture.CreateIndependentContext(barrier);
        await using var secondDb = fixture.CreateIndependentContext(barrier);
        var firstService = fixture.CreateIndependentService(firstDb);
        var secondService = fixture.CreateIndependentService(secondDb);

        var results = await Task.WhenAll(
            firstService.ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None),
            secondService.ConsumeDownloadAsync(prepared.Delivery.PublicId, prepared.Token!, CancellationToken.None));

        Assert.Single(results, item => item is not null);
        await using var verificationDb = fixture.CreateIndependentContext();
        var stored = await verificationDb.MessengerDocumentDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.DownloadCount);
        Assert.Equal(MessengerDocumentDeliveryStatuses.Read, stored.Status);
    }

    [Fact]
    public async Task OversizedDownloadTokenIsRejectedBeforeDatabaseQuery()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var recorder = new CommandCounter();
        await using var db = fixture.CreateIndependentContext(recorder);
        var service = fixture.CreateIndependentService(db);

        var result = await service.ConsumeDownloadAsync(
            Guid.NewGuid(),
            new string('x', 513),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, recorder.CommandCount);
    }

    [Fact]
    public async Task SecureDownloadUsesPostBodySecurityHeadersAndRemainsOneTime()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        var controller = new PublicMessengerDocumentDeliveriesController(
            fixture.Service,
            fixture.Db,
            fixture.Configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = Assert.IsType<PhysicalFileResult>(await controller.DownloadSecurely(
            prepared.Delivery.PublicId,
            new MessengerDocumentDownloadRequest { Token = prepared.Token! },
            CancellationToken.None));

        Assert.False(result.EnableRangeProcessing);
        Assert.Equal("no-store, private", controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
        Assert.Equal("noindex, nofollow, noarchive", controller.Response.Headers["X-Robots-Tag"]);
        Assert.IsType<NotFoundResult>(await controller.DownloadSecurely(
            prepared.Delivery.PublicId,
            new MessengerDocumentDownloadRequest { Token = prepared.Token! },
            CancellationToken.None));
    }

    [Fact]
    public async Task PrepareIsIdempotentForSameVersionRecipientAndChecksum()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();

        var first = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        var second = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);

        Assert.Equal(first.Delivery.Id, second.Delivery.Id);
        Assert.NotNull(second.Token);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Single(await fixture.Db.MessengerDocumentDeliveries.ToListAsync());
    }

    [Fact]
    public async Task PreparingUsedDocumentAgainStartsCleanDeliveryCycle()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var first = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Первый цикл", CancellationToken.None);
        Assert.NotNull(await fixture.Service.ConsumeDownloadAsync(
            first.Delivery.PublicId, first.Token!, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();

        var renewed = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Новый цикл", CancellationToken.None);

        Assert.Equal(first.Delivery.Id, renewed.Delivery.Id);
        Assert.NotEqual(first.Token, renewed.Token);
        Assert.Equal(MessengerDocumentDeliveryStatuses.Prepared, renewed.Delivery.Status);
        Assert.Equal(0, renewed.Delivery.DownloadCount);
        Assert.Equal(0, renewed.Delivery.AttemptCount);
        Assert.Null(renewed.Delivery.FirstOpenedAt);
        Assert.Null(renewed.Delivery.LastOpenedAt);
        Assert.Null(renewed.Delivery.ReadAt);
        Assert.Null(renewed.Delivery.DeliveredAt);
        Assert.Null(renewed.Delivery.FailedAt);
        Assert.Equal("Новый цикл", renewed.Delivery.MessagePreview);
        Assert.NotNull(await fixture.Service.ConsumeDownloadAsync(
            renewed.Delivery.PublicId, renewed.Token!, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareRejectsRecipientOfAnotherClient()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var otherClient = new Client { Name = "Другой клиент", IsActive = true };
        fixture.Db.Clients.Add(otherClient);
        await fixture.Db.SaveChangesAsync();
        var binding = await fixture.Service.CreateVerifiedBindingAsync(
            fixture.UserId, otherClient.Id, null, "telegram", "telegram-bot",
            "Другой", "987654", "admin-confirmed", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PrepareCaseDocumentAsync(
                fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));

        Assert.Contains("не принадлежит", error.Message);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.ToListAsync());
    }

    [Fact]
    public async Task GeneratedFinancialPdfCreatesImmutableSnapshotAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 generated invoice");

        var first = await fixture.Service.PrepareGeneratedDocumentAsync(
            fixture.UserId, "invoice", 55, 1, fixture.ClientId, "invoice-55.pdf",
            "application/pdf", new MemoryStream(pdf), pdf.Length, binding.Id, "Счёт", CancellationToken.None);
        var second = await fixture.Service.PrepareGeneratedDocumentAsync(
            fixture.UserId, "invoice", 55, 1, fixture.ClientId, "invoice-55.pdf",
            "application/pdf", new MemoryStream(pdf), pdf.Length, binding.Id, "Счёт повторно", CancellationToken.None);

        Assert.Equal(first.Delivery.Id, second.Delivery.Id);
        Assert.Equal("invoice", first.Delivery.DocumentType);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)), first.Delivery.ChecksumSha256);
        Assert.Single(await fixture.Db.MessengerDocumentDeliveries.ToListAsync());
        var download = await fixture.Service.ConsumeDownloadAsync(second.Delivery.PublicId, second.Token!, CancellationToken.None);
        Assert.NotNull(download);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(download.FullPath));
    }

    [Fact]
    public async Task OutboxSendsQueuedDeliveryOnceAndClearsProtectedToken()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, "Сообщение", CancellationToken.None);
        await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);
        var provider = new RecordingProvider();
        var processor = fixture.CreateProcessor(provider);

        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));

        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Sent, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.ProtectedLinkToken);
        Assert.Single(provider.Requests);
        Assert.Contains("https://crm.example.test/public/document/", provider.Requests[0].Link);
        Assert.Contains("#token=", provider.Requests[0].Link);
        Assert.DoesNotContain("?token=", provider.Requests[0].Link);
    }

    [Fact]
    public async Task OutboxSchedulesBackoffForTransientFailure()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);
        var processor = fixture.CreateProcessor(new RecordingProvider(transientFailure: true));

        await processor.ProcessBatchAsync(CancellationToken.None);

        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow);
        Assert.NotNull(delivery.ProtectedLinkToken);
    }

    [Fact]
    public async Task OutboxSchedulesBackoffWhenProviderTimesOut()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);

        await fixture.CreateProcessor(new TimeoutProvider()).ProcessBatchAsync(CancellationToken.None);

        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Failed, delivery.Status);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow);
        Assert.Contains("timeout", delivery.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutboxProcessesOnlyConfiguredBatchSize()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 batch");
        for (var index = 1; index <= 21; index++)
        {
            var prepared = await fixture.Service.PrepareGeneratedDocumentAsync(
                fixture.UserId, "invoice", 1000 + index, 1, fixture.ClientId, $"invoice-{index}.pdf",
                "application/pdf", new MemoryStream(pdf), pdf.Length, binding.Id, null, CancellationToken.None);
            await fixture.Service.QueueAsync(prepared.Delivery.Id, CancellationToken.None);
        }

        var provider = new RecordingProvider();
        var processed = await fixture.CreateProcessor(provider).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(20, processed);
        Assert.Equal(20, provider.Requests.Count);
        Assert.Equal(1, await fixture.Db.MessengerDocumentDeliveries.CountAsync(item => item.Status == MessengerDocumentDeliveryStatuses.Queued));
    }

    [Fact]
    public async Task GeneratedFinancialPdfAcceptsMaximumAllowedSize()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var pdf = new byte[25 * 1024 * 1024];
        "%PDF-"u8.CopyTo(pdf);

        var prepared = await fixture.Service.PrepareGeneratedDocumentAsync(
            fixture.UserId, "contract", 77, 1, fixture.ClientId, "contract-77.pdf",
            "application/pdf", new MemoryStream(pdf, writable: false), pdf.LongLength, binding.Id, null, CancellationToken.None);

        Assert.Equal(pdf.LongLength, prepared.Delivery.SizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)), prepared.Delivery.ChecksumSha256);
    }

    [Fact]
    public async Task ProviderCallbackUpdatesStatusMonotonically()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var prepared = await fixture.Service.PrepareCaseDocumentAsync(
            fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        prepared.Delivery.Status = MessengerDocumentDeliveryStatuses.Sent;
        prepared.Delivery.ProviderMessageId = "provider-message-1";
        await fixture.Db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessengerDocumentDelivery:Providers:Telegram:WebhookSecret"] = "callback-secret",
        }).Build();
        var controller = new PublicMessengerDocumentDeliveriesController(fixture.Service, fixture.Db, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.Headers["X-Document-Delivery-Webhook-Secret"] = "callback-secret";

        Assert.IsType<OkObjectResult>(await controller.Callback("TELEGRAM", new MessengerDocumentDeliveryCallbackRequest
        {
            ProviderMessageId = "provider-message-1",
            Status = "read",
        }, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();
        var delivery = await fixture.Db.MessengerDocumentDeliveries.SingleAsync();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Read, delivery.Status);
        Assert.NotNull(delivery.DeliveredAt);
        Assert.NotNull(delivery.ReadAt);

        controller.Request.Headers["X-Document-Delivery-Webhook-Secret"] = "callback-secret";
        Assert.IsType<OkObjectResult>(await controller.Callback("telegram", new MessengerDocumentDeliveryCallbackRequest
        {
            ProviderMessageId = "provider-message-1",
            Status = "failed",
            Error = "late failure",
        }, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(MessengerDocumentDeliveryStatuses.Read, (await fixture.Db.MessengerDocumentDeliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProviderCallbackRejectsUnknownProviderBeforeDatabaseQuery()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var counter = new CommandCounter();
        await using var db = fixture.CreateIndependentContext(counter);
        var controller = new PublicMessengerDocumentDeliveriesController(
            fixture.CreateIndependentService(db),
            db,
            fixture.Configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.Callback("unknown-provider", new MessengerDocumentDeliveryCallbackRequest
        {
            ProviderMessageId = "message-1",
            Status = "read",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, counter.CommandCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _root;
        private readonly string _databaseConnectionString;
        public PaymentContext Db { get; }
        public MessengerDocumentDeliveryService Service { get; }
        public int UserId { get; }
        public int ClientId { get; }
        public int DocumentId { get; }
        public string SourcePath { get; }
        public string SourceFullPath { get; }
        public string ExpectedContent { get; } = "публичный судебный документ";
        public string ExpectedChecksum { get; }
        public IConfiguration Configuration { get; }
        public IDataProtectionProvider DataProtectionProvider { get; }

        private Fixture(
            SqliteConnection connection,
            string root,
            PaymentContext db,
            MessengerDocumentDeliveryService service,
            int userId,
            int clientId,
            int documentId,
            string sourcePath,
            string sourceFullPath,
            string expectedChecksum,
            IConfiguration configuration,
            IDataProtectionProvider dataProtectionProvider,
            string databaseConnectionString)
        {
            _connection = connection;
            _root = root;
            Db = db;
            Service = service;
            UserId = userId;
            ClientId = clientId;
            DocumentId = documentId;
            SourcePath = sourcePath;
            SourceFullPath = sourceFullPath;
            ExpectedChecksum = expectedChecksum;
            Configuration = configuration;
            DataProtectionProvider = dataProtectionProvider;
            _databaseConnectionString = databaseConnectionString;
        }

        public static async Task<Fixture> CreateAsync(bool enabled, bool separateStorageRoot = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"payplanner-messenger-delivery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databaseConnectionString = $"Data Source={Path.Combine(root, "delivery-tests.db")};Default Timeout=10";
            var connection = new SqliteConnection(databaseConnectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "admin", Description = "Администратор" };
            var user = new User
            {
                Email = "admin@example.com",
                FullName = "Администратор",
                PasswordHash = "hash",
                Role = role,
                IsActive = true,
                IsApproved = true,
            };
            var client = new Client { Name = "Клиент", IsActive = true };
            var clientCase = new ClientCase { Client = client, Title = "Дело", CaseNumber = "1" };
            db.AddRange(user, clientCase);
            await db.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = separateStorageRoot ? Path.Combine(root, "files") : root,
                    ["MessengerDocumentDelivery:Enabled"] = enabled.ToString(),
                    ["MessengerDocumentDelivery:LinkLifetimeHours"] = "24",
                    ["MessengerDocumentDelivery:PublicBaseUrl"] = "https://crm.example.test",
                })
                .Build();
            var environment = new TestEnvironment(root);
            var storage = new LocalCaseDocumentStorage(environment, configuration);
            var sourcePath = storage.BuildRelativePath(clientCase.Id, "document.txt");
            var expectedContent = "публичный судебный документ";
            await storage.SaveAsync(sourcePath, System.Text.Encoding.UTF8.GetBytes(expectedContent), CancellationToken.None);
            var document = new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = Guid.NewGuid().ToString("N"),
                Version = 1,
                DocumentType = "court-act",
                Status = "approved",
                OriginalFileName = "document.txt",
                StoredFileName = "document.txt",
                StoragePath = sourcePath,
                ContentType = "text/plain",
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(expectedContent),
                UploadedByUserId = user.Id,
                UploadedByName = user.FullName,
            };
            db.ClientCaseDocuments.Add(document);
            await db.SaveChangesAsync();
            var provider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new MessengerDocumentDeliveryService(db, storage, provider, environment, configuration);
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedContent)));
            return new Fixture(
                connection, root, db, service, user.Id, client.Id, document.Id,
                sourcePath, storage.ResolvePath(sourcePath), checksum, configuration, provider,
                databaseConnectionString);
        }

        public PaymentContext CreateIndependentContext(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(_databaseConnectionString)
                .AddInterceptors(interceptors)
                .Options;
            return new PaymentContext(options, useObjectAuditModel: false);
        }

        public MessengerDocumentDeliveryService CreateIndependentService(PaymentContext db, IProductStorageReader? productStorageReader = null)
        {
            var environment = new TestEnvironment(_root);
            var storage = new LocalCaseDocumentStorage(environment, Configuration);
            return new MessengerDocumentDeliveryService(
                db,
                storage,
                DataProtectionProvider,
                environment,
                Configuration, productStorageReader);
        }

        public (MessengerDocumentDeliveryService Service, StorageOperationProcessor Worker, CountingLocalProvider Provider,
            TestStorageOptions Options) CreateManagedRuntime(PaymentContext db, bool loseAcknowledgement = false, int requiredCopies = 1)
        {
            var environment = new TestEnvironment(_root);
            var provider = new CountingLocalProvider(new LocalStorageProvider(environment, Configuration))
            { LoseAcknowledgement = loseAcknowledgement };
            var mirrorConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["StorageOrchestration:LocalRoot"] = Path.Combine(_root, "managed-mirror") }).Build();
            var mirror = new CountingLocalProvider(new LocalStorageProvider(environment, mirrorConfiguration), "mirror");
            var registry = new StorageProviderRegistry(requiredCopies == 1 ? [provider, new LegacyLocalStorageProvider(environment, Configuration)] : [provider, mirror, new LegacyLocalStorageProvider(environment, Configuration)]);
            var options = new TestStorageOptions(new()
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                ProductWriteEnabledClasses = ["messenger-delivery"],
                ProductReadEnabledClasses = ["messenger-delivery"],
                WorkerEnabled = true,
                ProviderConnections = new() { ["mirror"] = "owned" },
                DefaultPolicy = new()
                {
                    RequiredCopies = requiredCopies,
                    WriteProviders = requiredCopies == 1 ? ["local"] : ["local", "mirror"],
                    ReadProviders = requiredCopies == 1 ? ["local"] : ["local", "mirror"]
                }
            });
            var policy = new StoragePolicyService(options, registry);
            var storage = new StorageObjectService(db, policy, registry, options);
            var reader = new ProductStorageReader(db, storage, options);
            var service = new MessengerDocumentDeliveryService(db, new LocalCaseDocumentStorage(environment, Configuration),
                DataProtectionProvider, environment, Configuration, reader, managedStorage: storage,
                storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: options, stageCapacity: new StorageStageCapacityService(db, registry, options, environment, Configuration));
            return (service, new(db, registry, options, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance), provider, options);
        }

        public Task<MessengerRecipientBinding> CreateBindingAsync()
            => Service.CreateVerifiedBindingAsync(
                UserId, ClientId, null, "telegram", "telegram-bot",
                "Клиент", "123456789", "admin-confirmed", CancellationToken.None);

        public MessengerDocumentOutboxProcessor CreateProcessor(IMessengerDocumentProvider provider)
            => new(
                Db,
                Configuration,
                [provider],
                DataProtectionProvider,
                NullLogger<MessengerDocumentOutboxProcessor>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class TestStorageOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class CountingLocalProvider(IStorageProvider inner, string? key = null) : IStorageProvider, IStorageCapacityProvider
    {
        public int PutCalls { get; private set; }
        public bool LoseAcknowledgement { get; set; }
        public Func<StorageObjectMetadata, StorageObjectMetadata>? RewriteHead { get; set; }
        public string Key => key ?? inner.Key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, key is null ? connectionId : null, minimumFreeBytes, ct);
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            PutCalls++;
            var result = await inner.PutAsync(request, ct);
            if (LoseAcknowledgement && result.Status == StorageOperationStatus.Succeeded)
                return result with { Status = StorageOperationStatus.Unknown, Error = "Owned lost acknowledgement" };
            return result;
        }
        public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
        {
            var result = await inner.HeadAsync(locator, ct);
            return RewriteHead?.Invoke(result) ?? result;
        }
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }

    private sealed class ParallelSelectBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothQueriesStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readerCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MessengerDocumentDeliveries", StringComparison.Ordinal)
                && Interlocked.Increment(ref _readerCount) >= 2)
            {
                _bothQueriesStarted.TrySetResult();
            }

            await _bothQueriesStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingProvider(bool transientFailure = false) : IMessengerDocumentProvider
    {
        public string Channel => MessengerDocumentChannels.Telegram;
        public List<MessengerDocumentProviderRequest> Requests { get; } = [];
        public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(transientFailure
                ? new MessengerDocumentProviderResult(false, MessengerDocumentDeliveryStatuses.Failed, Error: "temporary", IsTransient: true)
                : new MessengerDocumentProviderResult(true, MessengerDocumentDeliveryStatuses.Sent, "provider-message-1"));
        }
    }

    private sealed class TimeoutProvider : IMessengerDocumentProvider
    {
        public string Channel => MessengerDocumentChannels.Telegram;
        public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct)
            => Task.FromException<MessengerDocumentProviderResult>(new TaskCanceledException("provider timeout"));
    }

    private sealed class RecordingMaxOutbox : IMaxOutboxService
    {
        public List<MaxQueueRequest> Requests { get; } = [];

        public Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new MaxOutboxMessage());
        }

        public Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct)
            => Task.FromResult(false);
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
