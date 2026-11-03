using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private sealed class MergeImmutableRuntime : IDisposable
    {
        private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "payplanner-merge-immutable-history"));
        public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public EphemeralDataProtectionProvider Protection { get; } = new();
        public ElectronicSignatureService Signature { get; }
        public MessengerDocumentDeliveryService Messenger { get; }
        public LocalCaseDocumentStorage Cases { get; }
        public IConfiguration Configuration { get; }
        public byte[] Pdf { get; } = "%PDF-1.7 synthetic merge evidence"u8.ToArray();

        public MergeImmutableRuntime(PaymentContext db)
        {
            Directory.CreateDirectory(Root);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Root,
                ["ElectronicSignature:StorageRoot"] = Path.Combine(Root, "esign"),
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["MessengerDocumentDelivery:Enabled"] = "true",
                ["MessengerDocumentDelivery:PublicBaseUrl"] = "https://crm.example.test",
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var environment = new TestWebHostEnvironment { ContentRootPath = Root, WebRootPath = Root };
            Cases = new LocalCaseDocumentStorage(environment, Configuration);
            Signature = new ElectronicSignatureService(db, Configuration, Protection, environment);
            Messenger = new MessengerDocumentDeliveryService(db, Cases, Protection, environment, Configuration);
        }

        public ElectronicSignaturePreparation SignatureRequest(MergeSeed seed, int clientId) => new("invoice", 1001, 1, clientId,
            ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc", 10, false, seed.Actor.Id,
            [new("Синтетический получатель", "recipient@example.test")]);

        public Task<PreparedMessengerDocumentDelivery> PrepareAsync(MergeSeed seed, int clientId, int bindingId)
            => Messenger.PrepareGeneratedDocumentAsync(seed.Actor.Id, "invoice", 1001, 1, clientId, "invoice.pdf", "application/pdf", new MemoryStream(Pdf), Pdf.Length, bindingId, "Синтетический документ", default);

        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(path), Parent, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(path), "N", out _)) throw new InvalidOperationException("Unexpected test root.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed record MergeImmutableSeed(ElectronicSignatureEnvelope Envelope, ElectronicSignaturePreparation Request,
        MessengerRecipientBinding Binding, PreparedMessengerDocumentDelivery Prepared);

    private static async Task<MergeImmutableSeed> SeedMergeImmutableAsync(PaymentContext db, MergeSeed seed, MergeImmutableRuntime runtime, string state = "prepared")
    {
        var request = runtime.SignatureRequest(seed, seed.Input.DuplicateSourceId);
        var envelope = await runtime.Signature.PrepareAsync(request, new MemoryStream(runtime.Pdf), "invoice.pdf", default);
        var binding = await runtime.Messenger.CreateVerifiedBindingAsync(seed.Actor.Id, seed.Input.DuplicateSourceId, null, "telegram", "synthetic-provider", "Синтетический получатель", "123456789", "synthetic-confirmation", default);
        var prepared = await runtime.PrepareAsync(seed, seed.Input.DuplicateSourceId, binding.Id);
        if (state != "prepared")
        {
            var now = DateTime.UtcNow;
            envelope.Status = state == "completed" ? ElectronicSignatureEnvelopeStatuses.Completed : ElectronicSignatureEnvelopeStatuses.Cancelled;
            envelope.ProviderEnvelopeId = "synthetic-provider-envelope";
            envelope.SentAt = now.AddMinutes(-1);
            envelope.CompletedAt = state == "completed" ? now : null;
            envelope.CancelledAt = state == "cancelled" ? now : null;
            foreach (var signer in envelope.Signers) { signer.Status = state == "completed" ? "signed" : "pending"; signer.SignedAt = state == "completed" ? now : null; }
            var artifactPath = Path.Combine(Path.GetDirectoryName(envelope.SnapshotPath)!, "synthetic-signature.p7s");
            byte[] artifact = "Synthetic signature evidence, not a real certificate"u8.ToArray();
            await File.WriteAllBytesAsync(artifactPath, artifact);
            db.ElectronicSignatureArtifacts.Add(new ElectronicSignatureArtifact { EnvelopeId = envelope.Id, ArtifactType = "signature", StoragePath = artifactPath, FileName = "synthetic-signature.p7s", ContentType = "application/pkcs7-signature", SizeBytes = artifact.Length, ChecksumSha256 = Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant(), VerificationStatus = "synthetic" });
            prepared.Delivery.Status = state == "completed" ? MessengerDocumentDeliveryStatuses.Sent : MessengerDocumentDeliveryStatuses.Cancelled;
            prepared.Delivery.ProviderMessageId = "synthetic-sent-message";
            prepared.Delivery.AttemptCount = 2;
            prepared.Delivery.SentAt = now;
            prepared.Delivery.CancelledAt = state == "cancelled" ? now : null;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(envelope, request, binding, prepared);
    }

    private static async Task<object[]> MergeImmutableRowsAsync<T>(PaymentContext db, bool omitClient) where T : class
    {
        var properties = db.Model.FindEntityType(typeof(T))!.GetProperties().OrderBy(x => x.Name).ToArray();
        Assert.All(properties, p => Assert.NotNull(p.PropertyInfo));
        var rows = await db.Set<T>().IgnoreQueryFilters().AsNoTracking().OrderBy(x => EF.Property<int>(x, "Id")).ToArrayAsync();
        return rows.Select(row => (object)properties.Where(p => !omitClient || p.Name != "ClientId").ToDictionary(p => p.Name, p => p.PropertyInfo!.GetValue(row))).ToArray();
    }

    private static async Task<string> MergeImmutableSnapshotAsync(PaymentContext db, bool omitClient = false) => JsonSerializer.Serialize(new
    {
        Envelopes = await MergeImmutableRowsAsync<ElectronicSignatureEnvelope>(db, omitClient),
        Signers = await MergeImmutableRowsAsync<ElectronicSignatureSigner>(db, false),
        Artifacts = await MergeImmutableRowsAsync<ElectronicSignatureArtifact>(db, false),
        Events = await MergeImmutableRowsAsync<ElectronicSignatureEvent>(db, false),
        Bindings = await MergeImmutableRowsAsync<MessengerRecipientBinding>(db, omitClient),
        Deliveries = await MergeImmutableRowsAsync<MessengerDocumentDelivery>(db, omitClient)
    });

    private static async Task<Dictionary<string, string>> MergeImmutableFilesAsync(PaymentContext db, MergeImmutableRuntime runtime)
    {
        var paths = (await db.ElectronicSignatureEnvelopes.AsNoTracking().Select(x => x.SnapshotPath).ToArrayAsync())
            .Concat(await db.ElectronicSignatureArtifacts.AsNoTracking().Select(x => x.StoragePath).ToArrayAsync())
            .Concat((await db.MessengerDocumentDeliveries.AsNoTracking().Select(x => x.SnapshotPath).ToArrayAsync()).Select(x => Path.Combine(runtime.Root, "messenger-document-deliveries", x)));
        var result = new Dictionary<string, string>();
        foreach (var path in paths) result.Add(path, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        return result;
    }

    private static async Task<string> MergeImmutableEvidenceAsync(MergeImmutableRuntime runtime, Guid id)
    {
        var package = await runtime.Signature.CreateEvidencePackageAsync(id, default);
        Assert.NotNull(package);
        using var archive = ZipFile.OpenRead(package!.FullPath);
        using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var json = JsonNode.Parse(await manifest.ReadToEndAsync())!.AsObject();
        Assert.True(json.Remove("generatedAt"));
        return json.ToJsonString();
    }

    public static IEnumerable<object[]> MergeImmutableHistoryCases() => from kind in new[] { "client", "external+client" } from state in new[] { "prepared", "completed", "cancelled" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, state, execute, postgres };

    [Theory]
    [MemberData(nameof(MergeImmutableHistoryCases))]
    public async Task MergeImmutableBoundary_PreservesEveryHistoryColumnAndEvidenceExceptCanonicalClient(string kind, string state, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, kind);
        using var runtime = new MergeImmutableRuntime(db);
        var history = await SeedMergeImmutableAsync(db, seed, runtime, state);
        var before = await MergeImmutableSnapshotAsync(db, execute);
        var files = await MergeImmutableFilesAsync(db, runtime);
        var evidence = await MergeImmutableEvidenceAsync(runtime, history.Envelope.PublicId);
        Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute));
        Assert.Equal(before, await MergeImmutableSnapshotAsync(db, execute));
        Assert.Equal(files, await MergeImmutableFilesAsync(db, runtime));
        Assert.Equal(evidence, await MergeImmutableEvidenceAsync(runtime, history.Envelope.PublicId));
        var envelope = await db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync();
        var binding = await db.MessengerRecipientBindings.AsNoTracking().SingleAsync();
        var delivery = await db.MessengerDocumentDeliveries.AsNoTracking().SingleAsync();
        var target = !execute ? seed.Input.DuplicateSourceId : kind == "client" ? seed.Input.PrimarySourceId : (await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == seed.Input.PrimarySourceId)).LinkedClientId;
        Assert.Equal(target, envelope.ClientId); Assert.Equal(target, binding.ClientId); Assert.Equal(target, delivery.ClientId);
        Assert.Equal("123456789", runtime.Protection.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1").Unprotect(binding.ProtectedAddress));
        Assert.Equal(history.Prepared.Token, runtime.Protection.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1").Unprotect(delivery.ProtectedLinkToken!));
        var signer = await db.ElectronicSignatureSigners.AsNoTracking().SingleAsync();
        Assert.Equal("recipient@example.test", runtime.Protection.CreateProtector("PayPlanner.ElectronicSignature.Contact.v1").Unprotect(signer.ProtectedContact));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class MergeImmutableProvider : IMessengerDocumentProvider
    {
        public string Channel => "telegram";
        public List<MessengerDocumentProviderRequest> Requests { get; } = [];
        public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(new MessengerDocumentProviderResult(true, MessengerDocumentDeliveryStatuses.Sent, "synthetic-result")); }
    }

    public static IEnumerable<object[]> MergeImmutableWorkflowCases() => from kind in new[] { "client", "external+client" } from mode in new[] { "signature-replay", "delivery-replay", "delivery-send", "case-send" } from postgres in new[] { false, true } select new object[] { kind, mode, postgres };

    [Theory]
    [MemberData(nameof(MergeImmutableWorkflowCases))]
    public async Task MergeImmutableBoundary_UsesCanonicalOwnerWithoutChangingSignerOrRecipient(string kind, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, kind);
        using var runtime = new MergeImmutableRuntime(db);
        var history = await SeedMergeImmutableAsync(db, seed, runtime);
        int? caseId = null;
        if (mode == "case-send")
        {
            var clientCase = new ClientCase { ClientId = seed.Input.DuplicateSourceId, Title = "Синтетическое дело", CaseNumber = "merge-evidence" };
            db.ClientCases.Add(clientCase); await db.SaveChangesAsync(); caseId = clientCase.Id;
            var path = runtime.Cases.BuildRelativePath(clientCase.Id, "original.pdf"); await runtime.Cases.SaveAsync(path, runtime.Pdf, default);
            var document = new ClientCaseDocument { ClientCaseId = clientCase.Id, DocumentKey = Guid.NewGuid().ToString("N"), DocumentType = "court-act", Status = "approved", OriginalFileName = "original.pdf", StoredFileName = "original.pdf", StoragePath = path, ContentType = "application/pdf", SizeBytes = runtime.Pdf.Length, UploadedByUserId = seed.Actor.Id, UploadedByName = seed.Actor.FullName };
            db.ClientCaseDocuments.Add(document); await db.SaveChangesAsync();
            history = history with { Prepared = await runtime.Messenger.PrepareCaseDocumentAsync(seed.Actor.Id, document.Id, history.Binding.Id, "Синтетический документ", default) };
        }
        db.ChangeTracker.Clear();
        var files = await MergeImmutableFilesAsync(db, runtime);
        Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, true));
        var target = (await db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).ClientId!.Value;
        if (mode == "signature-replay")
        {
            var replay = await runtime.Signature.PrepareAsync(history.Request with { ClientId = target }, new MemoryStream(runtime.Pdf), "invoice.pdf", default);
            Assert.Equal(history.Envelope.Id, replay.Id); Assert.Equal(history.Envelope.PublicId, replay.PublicId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Signature.PrepareAsync(history.Request, new MemoryStream(runtime.Pdf), "invoice.pdf", default));
            Assert.Single(await db.ElectronicSignatureEnvelopes.ToArrayAsync());
        }
        else if (mode == "delivery-replay")
        {
            var replay = await runtime.PrepareAsync(seed, target, history.Binding.Id);
            Assert.Equal(history.Prepared.Delivery.Id, replay.Delivery.Id); Assert.Equal(history.Prepared.Delivery.SnapshotPath, replay.Delivery.SnapshotPath);
            Assert.NotEqual(history.Prepared.Token, replay.Token);
            Assert.Null(await runtime.Messenger.ConsumeDownloadAsync(replay.Delivery.PublicId, history.Prepared.Token!, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PrepareAsync(seed, seed.Input.DuplicateSourceId, history.Binding.Id));
            Assert.Single(await db.MessengerDocumentDeliveries.ToArrayAsync());
        }
        else
        {
            if (caseId.HasValue) Assert.Equal(target, await db.ClientCases.Where(x => x.Id == caseId).Select(x => x.ClientId).SingleAsync());
            var provider = new MergeImmutableProvider();
            await runtime.Messenger.QueueAsync(history.Prepared.Delivery.Id, default);
            var processor = new MessengerDocumentOutboxProcessor(db, runtime.Configuration, [provider], runtime.Protection, NullLogger<MessengerDocumentOutboxProcessor>.Instance);
            Assert.Equal(1, await processor.ProcessBatchAsync(default));
            var sent = Assert.Single(provider.Requests);
            Assert.Equal("123456789", sent.RecipientAddress); Assert.Equal(history.Prepared.Delivery.IdempotencyKey, sent.IdempotencyKey);
            Assert.EndsWith("#token=" + Uri.EscapeDataString(history.Prepared.Token!), sent.Link);
            var download = await runtime.Messenger.ConsumeDownloadAsync(history.Prepared.Delivery.PublicId, history.Prepared.Token!, default);
            Assert.NotNull(download);
            await using var content = download!.Content ?? File.OpenRead(download.FullPath);
            using var copy = new MemoryStream(); await content.CopyToAsync(copy); Assert.Equal(runtime.Pdf, copy.ToArray());
            Assert.Null(await runtime.Messenger.ConsumeDownloadAsync(history.Prepared.Delivery.PublicId, history.Prepared.Token!, default));
        }
        Assert.Equal(files, await MergeImmutableFilesAsync(db, runtime));
    }

    private sealed class MergeImmutableInjectedFailure : Exception;
    private sealed class MergeImmutableAfterCommitWrite(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Reached { get; private set; }
        public Action? Cancel { get; set; }
        public int? ObservedClientId { get; private set; }
        private bool merge;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            merge = Armed && data.Context!.ChangeTracker.Entries<ObjectAuditEvent>().Any(x => x.State == EntityState.Added && x.Entity.ObjectType == "contact-merge");
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!merge) return result;
            Reached = true; ObservedClientId = await ((PaymentContext)data.Context!).ElectronicSignatureEnvelopes.AsNoTracking().Select(x => x.ClientId).SingleAsync(ct);
            if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); }
            throw new MergeImmutableInjectedFailure();
        }
    }

    public static IEnumerable<object[]> MergeImmutableRollbackCases() => from kind in new[] { "client", "external+client" } from cancel in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, cancel, postgres };

    [Theory]
    [MemberData(nameof(MergeImmutableRollbackCases))]
    public async Task MergeImmutableBoundary_RollsBackAlreadyMovedHistoryAfterLastSqlWrite(string kind, bool cancel, bool postgres)
    {
        var fault = new MergeImmutableAfterCommitWrite(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault);
        var seed = await SeedMergeBoundaryAsync(db, kind);
        using var runtime = new MergeImmutableRuntime(db);
        await SeedMergeImmutableAsync(db, seed, runtime, "completed");
        var before = await MergeImmutableSnapshotAsync(db); var source = await MergeSnapshotAsync(db); var files = await MergeImmutableFilesAsync(db, runtime);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => RunMergeAsync(db, seed, true, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<MergeImmutableInjectedFailure>(Write);
        Assert.True(fault.Reached); Assert.NotEqual(seed.Input.DuplicateSourceId, fault.ObservedClientId);
        Assert.Equal(before, await MergeImmutableSnapshotAsync(db)); Assert.Equal(source, await MergeSnapshotAsync(db)); Assert.Equal(files, await MergeImmutableFilesAsync(db, runtime));
        Assert.Null(db.Database.CurrentTransaction);
    }
}
