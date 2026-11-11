using System.Diagnostics;
using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using PayPlanner.Api.Tests;

internal static class StageWriterProbe
{
    public static async Task Main(string[] args)
    {
        if (args is ["identity", var identityRoot])
        {
            var ownedRoot = ValidateRoot(identityRoot);
            RequireMailIsolation();
            using var file = new FileStream(Path.Combine(ownedRoot, "identity.tmp"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Console.WriteLine(JsonSerializer.Serialize(StorageStageFileIdentity.Observe(file.SafeFileHandle)
                ?? throw new IOException("Native file identity is unavailable.")));
            return;
        }
        if (args.Length != 3 || args[0] is not ("sqlite" or "postgres")
            || args[2] is not ("missing" or "partial" or "complete" or "checkpoint-empty" or "checkpoint-planned" or "checkpoint-complete" or "checkpoint-partial-first" or "checkpoint-partial-next" or "original-empty" or "original-planned" or "original-complete" or "original-partial-first" or "original-partial-next" or "original-metrics" or "evidence-partial-first" or "evidence-partial-next" or "evidence-metrics" or "pdf-partial-first" or "pdf-partial-next" or "pdf-metrics"))
            throw new ArgumentException("Owned stage probe arguments are invalid.");
        var root = ValidateRoot(args[1]);
        var name = Path.GetFileName(root);
        RequireMailIsolation();
        var partialBlock = args[2] is "checkpoint-partial-first" or "checkpoint-partial-next";
        var checkpointClock = Stopwatch.StartNew();
        ReportCheckpoint("startup");
        var pause = new CheckpointPause();
        var queries = new CheckpointQueryCounts();
        await using PaymentContext db = args[0] == "sqlite"
            ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").AddInterceptors(pause, queries).Options)
            : CreatePostgres(name, pause, queries);
        if (!await db.SystemSettings.AnyAsync(row => row.Key == "notifications.email.enabled" && row.Value == "false"))
            throw new InvalidOperationException("Owned fixture must disable database email.");
        ReportCheckpoint("database-ready");
        var storageRoot = Path.Combine(root, "objects");
        var stageDirectory = Path.Combine(storageRoot, ".process-stage");
        Directory.CreateDirectory(stageDirectory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = storageRoot,
            ["ElectronicSignature:StorageRoot"] = (args[2].StartsWith("evidence-", StringComparison.Ordinal) || args[2].StartsWith("pdf-", StringComparison.Ordinal)) ? storageRoot : null,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var environment = new ProbeEnvironment(root);
        var options = new ProbeOptions();
        List<IStorageProvider> probeProviders = [new LocalStorageProvider(environment, configuration)];
        if (args[2].StartsWith("evidence-", StringComparison.Ordinal) || args[2].StartsWith("pdf-", StringComparison.Ordinal))
            probeProviders.Add(new LegacyLocalStorageProvider(environment, configuration));
        var registry = new StorageProviderRegistry(probeProviders);
        var stages = new StorageStageCapacityService(db, registry, options, environment, configuration);
        if (args[2].StartsWith("evidence-", StringComparison.Ordinal))
        {
            var original = await File.ReadAllBytesAsync(Path.Combine(root, "evidence-owned-source.pdf"));
            if (original.Length != StorageStageFileReceipt.MaximumBlockBytes * (args[2] == "evidence-metrics" ? 4 : 2) + 4
                || !original.AsSpan(0, 8).SequenceEqual("%PDF-1.7"u8))
                throw new InvalidOperationException("Owned evidence source is invalid.");
            var user = new User
            {
                Email = "owned-probe@example.test",
                FullName = "Owned probe",
                PasswordHash = "synthetic",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "admin", Description = "Owned probe" }
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var preparation = new ElectronicSignatureService(db, configuration, protection, environment);
            var envelope = await preparation.PrepareAsync(new("invoice", 101, 1, null,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc", 10, false,
                user.Id, [new ElectronicSignatureSignerDraft("Owned signer", "owned-signer@example.test")]),
                new MemoryStream(original), "owned.pdf", default);
            if (args[2] == "evidence-metrics")
            {
                var sourceSha = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
                var source = new EvidenceFileReader(envelope.SnapshotPath, original.Length, sourceSha);
                StorageStageCapacityTicket? measuredTicket = null;
                long allocatedBefore = 0;
                ProcessIo? ioBefore = null;
                var measuredTime = new Stopwatch();
                var measuredStages = new PartialOriginalStages(stages, (ownedTicket, _) =>
                {
                    measuredTicket = ownedTicket;
                    queries.Reservations = queries.Copies = queries.Total = pause.Commits = 0;
                    allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    ioBefore = ReadProcessIo();
                    measuredTime.Start();
                });
                var producer = new ElectronicSignatureService(db, configuration, protection, environment, source,
                    storageOptions: options, stageCapacity: measuredStages);
                var package = await producer.CreateEvidencePackageAsync(envelope.PublicId, default)
                    ?? throw new InvalidOperationException("Measured evidence package is missing.");
                var opening = (Task<FileStream>)typeof(ElectronicSignatureEvidencePackage)
                    .GetMethod("OpenReadAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(package, [CancellationToken.None])!;
                await using var verified = await opening;
                measuredTime.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                var ioAfter = ReadProcessIo();
                if (measuredTicket is null || source.Content is null || source.Content.CanRead || verified.CanWrite)
                    throw new InvalidOperationException("Measured evidence did not close its source or return readonly handoff.");
                // Extra archive/source validation is deliberately outside the
                // measured window and uses the already held native descriptor.
                verified.Position = 0;
                var handoffSha = Convert.ToHexString(await SHA256.HashDataAsync(verified)).ToLowerInvariant();
                verified.Position = 0;
                using (var archive = new ZipArchive(verified, ZipArchiveMode.Read, leaveOpen: true))
                {
                    if (archive.GetEntry("manifest.json") is null) throw new InvalidOperationException("Evidence manifest is missing.");
                    await using var input = archive.GetEntry("original/document.pdf")?.Open()
                        ?? throw new InvalidOperationException("Evidence original entry is missing.");
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant() != sourceSha)
                        throw new InvalidOperationException("Evidence original entry differs from the owned snapshot.");
                }
                verified.Position = 0;
                using var process = Process.GetCurrentProcess();
                await WriteCheckpointReadyAsync(root, measuredTicket, new
                {
                    HandoffSize = verified.Length,
                    HandoffCanWrite = verified.CanWrite,
                    HandoffSha256 = handoffSha,
                    SourceBytes = source.Content.BytesRead,
                    SourceReads = source.Content.ReadCalls,
                    SourceOpens = source.Opens,
                    SourceClosed = !source.Content.CanRead,
                    SourcePath = envelope.SnapshotPath,
                    SourceSha256 = sourceSha,
                    ReservationGroups = queries.Reservations,
                    CopyGroups = queries.Copies,
                    TotalSqlCommands = queries.Total,
                    Commits = pause.Commits,
                    AllocatedBytes = allocated,
                    ManagedHeapBytes = GC.GetTotalMemory(false),
                    WorkingSetBytes = process.WorkingSet64,
                    PeakWorkingSetBytes = process.PeakWorkingSet64,
                    ElapsedMs = measuredTime.ElapsedMilliseconds,
                    ProcessReadOperations = Difference(ioBefore?.ReadOperations, ioAfter?.ReadOperations),
                    ProcessWriteOperations = Difference(ioBefore?.WriteOperations, ioAfter?.WriteOperations),
                    ProcessReadBytes = Difference(ioBefore?.ReadBytes, ioAfter?.ReadBytes),
                    ProcessWriteBytes = Difference(ioBefore?.WriteBytes, ioAfter?.WriteBytes),
                    IoScope = "Windows process logical I/O including source file, archive verification, filesystem and database; not physical disk traffic. Window starts after initial durable receipt/coalescing buffer allocation and ends at verified readonly HTTP handoff. Owned file-backed source is opened/read once inside window. Fixture, PDF preparation/model setup are excluded. Extra held-stream ZIP/SHA validation is excluded from time/allocated/I/O, included in heap/working-set."
                });
                await Task.Delay(Timeout.Infinite);
                return;
            }
            Stream? facade = null;
            var evidenceStages = new PartialOriginalStages(stages, (ownedTicket, checkpoint) =>
            {
                var descriptor = typeof(StorageStageCheckpointWriter).GetField("_file",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(checkpoint) as FileStream
                    ?? throw new InvalidOperationException("Owned evidence descriptor is unavailable.");
                async Task Fault()
                {
                    await using PaymentContext observer = args[0] == "sqlite"
                        ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                            .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").Options)
                        : CreatePostgres(name);
                    var current = await observer.StorageCapacityReservations.AsNoTracking().SingleAsync();
                    if (!StorageStageFileReceipt.TryParse(current.StageFileReceiptJson, out var planned)
                        || planned!.FileIdentity != StorageStageFileIdentity.Observe(descriptor.SafeFileHandle)
                        || descriptor.Length != planned.PreviousBytes || descriptor.Position != planned.PreviousBytes)
                        throw new InvalidOperationException("Evidence fault requires the committed original boundary.");
                    var sequence = args[2] == "evidence-partial-next" ? 2 : 1;
                    if (planned.Sequence < sequence) { pause.AfterCommitted = Fault; return; }
                    if (planned.Sequence != sequence || planned.PlannedBytes - planned.PreviousBytes != StorageStageFileReceipt.MaximumBlockBytes)
                        throw new InvalidOperationException("Unexpected evidence block boundary.");
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var buffer = facade?.GetType().GetField("_buffer", flags)?.GetValue(facade) as byte[];
                    var count = facade?.GetType().GetField("_count", flags)?.GetValue(facade) as int?;
                    if (buffer is null || count != StorageStageFileReceipt.MaximumBlockBytes)
                        throw new InvalidOperationException("Actual pending ZIP block is unavailable.");
                    var plannedBytes = new byte[(int)planned.PlannedBytes];
                    descriptor.Position = 0;
                    await descriptor.ReadExactlyAsync(plannedBytes.AsMemory(0, (int)planned.PreviousBytes));
                    descriptor.Position = planned.PreviousBytes;
                    buffer.AsMemory(0, count.Value).CopyTo(plannedBytes.AsMemory((int)planned.PreviousBytes));
                    if (Convert.ToHexString(SHA256.HashData(plannedBytes)).ToLowerInvariant() != planned.PlannedSha256)
                        throw new InvalidOperationException("Pending ZIP buffer differs from committed planned SHA.");
                    await File.WriteAllBytesAsync(Path.Combine(root, "evidence-planned.zip"), plannedBytes);
                    // Controlled partial I/O through the actual ZIP descriptor
                    // after planned SQL commit. Not an OS WriteAsync interruption.
                    await descriptor.WriteAsync(buffer.AsMemory(0, StorageStageFileReceipt.MaximumBlockBytes / 2));
                    await descriptor.FlushAsync();
                    descriptor.Flush(flushToDisk: true);
                    await WriteCheckpointReadyAsync(root, ownedTicket, new
                    {
                        SourcePath = envelope.SnapshotPath,
                        SourceSize = original.Length,
                        SourceSha256 = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()
                    });
                    await Task.Delay(Timeout.Infinite);
                }
                pause.AfterCommitted = Fault;
            }, output => facade = output);
            var evidence = new ElectronicSignatureService(db, configuration, protection, environment,
                storageOptions: options, stageCapacity: evidenceStages);
            await evidence.CreateEvidencePackageAsync(envelope.PublicId, default);
            throw new InvalidOperationException("Evidence process probe did not pause.");
        }
        if (args[2].StartsWith("pdf-", StringComparison.Ordinal))
        {
            var metricsMode = args[2] == "pdf-metrics";
            var original = metricsMode ? [] : await File.ReadAllBytesAsync(Path.Combine(root, "pdf-owned-source.pdf"));
            if (!metricsMode && (original.Length != StorageStageFileReceipt.MaximumBlockBytes * 2 + 4
                || !original.AsSpan(0, 8).SequenceEqual("%PDF-1.7"u8)))
                throw new InvalidOperationException("Owned PDF source is invalid.");
            var user = new User
            {
                Email = "owned-probe@example.test",
                FullName = "Owned probe",
                PasswordHash = "synthetic",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "admin", Description = "Owned probe" }
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            if (metricsMode)
            {
                var source = new CountedEvidenceFile(Path.Combine(root, "pdf-owned-source.pdf"));
                await using var ownedSource = source;
                if (source.Length != StorageStageFileReceipt.MaximumBlockBytes * 4 + 4)
                    throw new InvalidOperationException("Owned PDF resource source has an invalid size.");
                StorageStageCapacityTicket? measuredTicket = null;
                long allocatedBefore = 0;
                ProcessIo? ioBefore = null;
                var measuredTime = new Stopwatch();
                var measuredStages = new PartialOriginalStages(stages, (ticket, _) =>
                {
                    measuredTicket = ticket;
                    queries.Reservations = queries.Copies = queries.Total = pause.Commits = 0;
                    allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    ioBefore = ReadProcessIo();
                    measuredTime.Start();
                });
                var measuredService = new ElectronicSignatureService(db, configuration, protection, environment,
                    storageOptions: options, stageCapacity: measuredStages);
                var envelope = await measuredService.PrepareAsync(new("invoice", 101, 1, null,
                    ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc", 10, false,
                    user.Id, [new ElectronicSignatureSignerDraft("Owned signer", "owned-signer@example.test")]),
                    source, "owned.pdf", default);
                await source.DisposeAsync(); // Caller owns the input stream.
                measuredTime.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                var ioAfter = ReadProcessIo();
                if (measuredTicket is null || source.CanRead)
                    throw new InvalidOperationException("Measured PDF source or ticket is invalid.");
                // Independent verification is outside the measured window.
                await using (var verified = File.OpenRead(envelope.SnapshotPath))
                    if (Convert.ToHexString(await SHA256.HashDataAsync(verified)).ToLowerInvariant() != envelope.ChecksumSha256)
                        throw new InvalidOperationException("Measured PDF snapshot differs from its checksum.");
                using var process = Process.GetCurrentProcess();
                await WriteCheckpointReadyAsync(root, measuredTicket, new
                {
                    HandoffSize = envelope.SizeBytes,
                    HandoffSha256 = envelope.ChecksumSha256,
                    SourceBytes = source.BytesRead,
                    SourceReads = source.ReadCalls,
                    SourceOpens = 1,
                    SourceClosed = !source.CanRead,
                    SnapshotPath = envelope.SnapshotPath,
                    ReservationGroups = queries.Reservations,
                    CopyGroups = queries.Copies,
                    TotalSqlCommands = queries.Total,
                    Commits = pause.Commits,
                    AllocatedBytes = allocated,
                    ManagedHeapBytes = GC.GetTotalMemory(false),
                    WorkingSetBytes = process.WorkingSet64,
                    PeakWorkingSetBytes = process.PeakWorkingSet64,
                    ElapsedMs = measuredTime.ElapsedMilliseconds,
                    ProcessReadOperations = Difference(ioBefore?.ReadOperations, ioAfter?.ReadOperations),
                    ProcessWriteOperations = Difference(ioBefore?.WriteOperations, ioAfter?.WriteOperations),
                    ProcessReadBytes = Difference(ioBefore?.ReadBytes, ioAfter?.ReadBytes),
                    ProcessWriteBytes = Difference(ioBefore?.WriteBytes, ioAfter?.WriteBytes),
                    IoScope = "Windows process logical I/O including source, PDF verification, filesystem and database; not physical disk traffic. Window starts after initial durable receipt/coalescing buffer allocation and ends after PrepareAsync returns and the caller closes its input. File opening, fixture/model setup and extra snapshot SHA verification are excluded. Extra verification is included in heap/working-set."
                });
                await Task.Delay(Timeout.Infinite);
                return;
            }
            Stream? facade = null;
            var pdfStages = new PartialOriginalStages(stages, (ownedTicket, checkpoint) =>
            {
                var descriptor = typeof(StorageStageCheckpointWriter).GetField("_file",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(checkpoint) as FileStream
                    ?? throw new InvalidOperationException("Owned PDF descriptor is unavailable.");
                async Task Fault()
                {
                    await using PaymentContext observer = args[0] == "sqlite"
                        ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                            .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").Options)
                        : CreatePostgres(name);
                    var current = await observer.StorageCapacityReservations.AsNoTracking().SingleAsync();
                    if (!StorageStageFileReceipt.TryParse(current.StageFileReceiptJson, out var planned)
                        || planned!.FileIdentity != StorageStageFileIdentity.Observe(descriptor.SafeFileHandle)
                        || descriptor.Length != planned.PreviousBytes || descriptor.Position != planned.PreviousBytes)
                        throw new InvalidOperationException("PDF fault requires the committed original boundary.");
                    var sequence = args[2] == "pdf-partial-next" ? 2 : 1;
                    if (planned.Sequence < sequence) { pause.AfterCommitted = Fault; return; }
                    if (planned.Sequence != sequence || planned.PlannedBytes - planned.PreviousBytes != StorageStageFileReceipt.MaximumBlockBytes)
                        throw new InvalidOperationException("Unexpected PDF block boundary.");
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var buffer = facade?.GetType().GetField("_buffer", flags)?.GetValue(facade) as byte[];
                    var count = facade?.GetType().GetField("_count", flags)?.GetValue(facade) as int?;
                    if (buffer is null || count != StorageStageFileReceipt.MaximumBlockBytes)
                        throw new InvalidOperationException("Actual pending PDF block is unavailable.");
                    var plannedBytes = new byte[(int)planned.PlannedBytes];
                    descriptor.Position = 0;
                    await descriptor.ReadExactlyAsync(plannedBytes.AsMemory(0, (int)planned.PreviousBytes));
                    descriptor.Position = planned.PreviousBytes;
                    buffer.AsMemory(0, count.Value).CopyTo(plannedBytes.AsMemory((int)planned.PreviousBytes));
                    if (Convert.ToHexString(SHA256.HashData(plannedBytes)).ToLowerInvariant() != planned.PlannedSha256)
                        throw new InvalidOperationException("Pending PDF buffer differs from committed planned SHA.");
                    await File.WriteAllBytesAsync(Path.Combine(root, "pdf-planned.pdf"), plannedBytes);
                    // Controlled partial I/O through the actual PDF descriptor
                    // after planned SQL commit. Not an OS WriteAsync interruption.
                    await descriptor.WriteAsync(buffer.AsMemory(0, StorageStageFileReceipt.MaximumBlockBytes / 2));
                    await descriptor.FlushAsync();
                    descriptor.Flush(flushToDisk: true);
                    await WriteCheckpointReadyAsync(root, ownedTicket, new
                    {
                        SourcePath = Path.Combine(root, "pdf-owned-source.pdf"),
                        SourceSize = original.Length,
                        SourceSha256 = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()
                    });
                    await Task.Delay(Timeout.Infinite);
                }
                pause.AfterCommitted = Fault;
            }, output => facade = output);
            var producer = new ElectronicSignatureService(db, configuration, protection, environment,
                storageOptions: options, stageCapacity: pdfStages);
            await producer.PrepareAsync(new("invoice", 101, 1, null,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc", 10, false,
                user.Id, [new ElectronicSignatureSignerDraft("Owned signer", "owned-signer@example.test")]),
                new MemoryStream(original), "owned.pdf", default);
            throw new InvalidOperationException("PDF process probe did not pause.");
        }
        if (args[2].StartsWith("original-", StringComparison.Ordinal))
        {
            var partialOriginal = args[2] is "original-partial-first" or "original-partial-next";
            var metricsMode = args[2] == "original-metrics";
            var original = metricsMode ? new byte[StorageStageFileReceipt.MaximumBlockBytes * 4 + 4] : partialOriginal
                ? new byte[StorageStageFileReceipt.MaximumBlockBytes * (args[2] == "original-partial-next" ? 2 : 1)]
                : new byte[] { 1, 2, 3, 4 };
            if (partialOriginal)
            {
                Array.Fill(original, (byte)0x31, 0, StorageStageFileReceipt.MaximumBlockBytes);
                if (args[2] == "original-partial-next")
                    Array.Fill(original, (byte)0x52, StorageStageFileReceipt.MaximumBlockBytes, StorageStageFileReceipt.MaximumBlockBytes);
            }
            IStorageStageCapacityService originalStages = stages;
            StorageStageCapacityTicket? measuredTicket = null;
            OriginalSource? measuredSource = null;
            long allocatedBefore = 0;
            ProcessIo? ioBefore = null;
            var measuredTime = new Stopwatch();
            if (metricsMode)
                originalStages = new PartialOriginalStages(stages, (ownedTicket, _) =>
                {
                    measuredTicket = ownedTicket;
                    queries.Reservations = queries.Copies = pause.Commits = 0;
                    allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    ioBefore = ReadProcessIo();
                    measuredTime.Start();
                });
            if (partialOriginal)
                originalStages = new PartialOriginalStages(stages, (ownedTicket, checkpoint) =>
                {
                    var descriptor = typeof(StorageStageCheckpointWriter).GetField("_file",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(checkpoint) as FileStream
                        ?? throw new InvalidOperationException("Owned original-read descriptor is unavailable.");
                    async Task Fault()
                    {
                        await using PaymentContext observer = args[0] == "sqlite"
                            ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                                .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").Options)
                            : CreatePostgres(name);
                        var current = await observer.StorageCapacityReservations.AsNoTracking().SingleAsync();
                        if (!StorageStageFileReceipt.TryParse(current.StageFileReceiptJson, out var planned)
                            || planned!.FileIdentity != StorageStageFileIdentity.Observe(descriptor.SafeFileHandle)
                            || descriptor.Length != planned.PreviousBytes || descriptor.Position != planned.PreviousBytes)
                            throw new InvalidOperationException("Partial original-read fault requires the committed original boundary.");
                        if (planned.PlannedBytes != original.LongLength) { pause.AfterCommitted = Fault; return; }
                        // Controlled partial I/O through the actual producer's
                        // owned descriptor; do not change production write code.
                        await descriptor.WriteAsync(original.AsMemory((int)planned.PreviousBytes, StorageStageFileReceipt.MaximumBlockBytes / 2));
                        await descriptor.FlushAsync();
                        descriptor.Flush(flushToDisk: true);
                        await WriteCheckpointReadyAsync(root, ownedTicket);
                        await Task.Delay(Timeout.Infinite);
                    }
                    pause.AfterCommitted = Fault;
                });
            var reader = new StorageOriginalReadStagingService(originalStages, environment, configuration, options,
                NullLogger<StorageOriginalReadStagingService>.Instance);
            var verified = await reader.VerifyAsync("process-probe:" + name, "owned-original", original.Length,
                Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), _ =>
                {
                    var source = new OriginalSource(original, async () =>
                    {
                        var row = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
                        if (row.StageWriterAdmissionPending || !StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var initial)
                            || initial!.Sequence != 0 || initial.PlannedBytes != 0)
                            throw new InvalidOperationException("Original source needs the committed initial receipt.");
                        var originalPath = Path.GetFullPath(Path.Combine(storageRoot, row.LocalLocator));
                        if (!originalPath.StartsWith(Path.GetFullPath(storageRoot) + Path.DirectorySeparatorChar,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new InvalidOperationException("Original stage is outside the owned root.");
                        var originalTicket = new StorageStageCapacityTicket(row.IdempotencyKey, row.BindingHash, row.ResourceKey,
                            row.GroupId, row.LocalLocator, originalPath, row.Bytes, row.ObjectId)
                        { WriterMarkerIdentity = row.StageWriterMarkerIdentity, RootIdentity = row.StageRootIdentity };
                        if (partialOriginal || metricsMode) return;
                        async Task SignalAndWait() { await WriteCheckpointReadyAsync(root, originalTicket); await Task.Delay(Timeout.Infinite); }
                        if (args[2] == "original-empty") await SignalAndWait();
                        else if (args[2] == "original-planned") pause.AfterCommitted = SignalAndWait;
                        else pause.AfterCommitted = () => { pause.BeforeStarting = SignalAndWait; return Task.CompletedTask; };
                    });
                    measuredSource = source;
                    return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, original.Length));
                }, default);
            if (metricsMode)
            {
                measuredTime.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                var ioAfter = ReadProcessIo();
                if (verified is null || measuredTicket is null || measuredSource is null || verified.CanWrite)
                    throw new InvalidOperationException("Measured original-read did not complete its readonly handoff.");
                // Windows handoff is already marked for deletion on its held
                // handle. Validate that stream, never reopen the protected path.
                // This extra test read happens AFTER the measured I/O window.
                verified.Position = 0;
                var handoffSha = Convert.ToHexString(await SHA256.HashDataAsync(verified)).ToLowerInvariant();
                verified.Position = 0;
                using var process = Process.GetCurrentProcess();
                var metrics = new
                {
                    HandoffSize = verified.Length,
                    HandoffCanWrite = verified.CanWrite,
                    HandoffSha256 = handoffSha,
                    SourceBytes = measuredSource.BytesRead,
                    SourceReads = measuredSource.ReadCalls,
                    ReservationGroups = queries.Reservations,
                    CopyGroups = queries.Copies,
                    Commits = pause.Commits,
                    AllocatedBytes = allocated,
                    ManagedHeapBytes = GC.GetTotalMemory(false),
                    WorkingSetBytes = process.WorkingSet64,
                    PeakWorkingSetBytes = process.PeakWorkingSet64,
                    ElapsedMs = measuredTime.ElapsedMilliseconds,
                    ProcessReadOperations = Difference(ioBefore?.ReadOperations, ioAfter?.ReadOperations),
                    ProcessWriteOperations = Difference(ioBefore?.WriteOperations, ioAfter?.WriteOperations),
                    ProcessReadBytes = Difference(ioBefore?.ReadBytes, ioAfter?.ReadBytes),
                    ProcessWriteBytes = Difference(ioBefore?.WriteBytes, ioAfter?.WriteBytes),
                    IoScope = "Windows process logical I/O, including filesystem, database and other process I/O; not physical disk traffic. Memory covers this child process, not only writer buffers. Window starts after initial receipt/coalescing buffer allocation and ends at readonly handoff. Extra test SHA through held handoff stream is excluded from time/allocated/I/O deltas but included in heap/working-set observations."
                };
                await WriteCheckpointReadyAsync(root, measuredTicket, metrics);
                await Task.Delay(Timeout.Infinite);
                await verified.DisposeAsync();
                return;
            }
            throw new InvalidOperationException("Original producer unexpectedly passed its pause boundary.");
        }
        var path = Path.Combine(stageDirectory, "original.tmp");
        var upper = partialBlock ? StorageStageFileReceipt.MaximumBlockBytes * (args[2] == "checkpoint-partial-next" ? 2L : 1L) : 4;
        var admission = await stages.ReserveAsync("process-probe:" + name, path, upper, default);
        var ticket = admission.Ticket ?? throw new InvalidOperationException(admission.Error);
        ReportCheckpoint("reservation-committed");
        try
        {
            if (args[2].StartsWith("checkpoint-", StringComparison.Ordinal))
            {
                await using var checkpoint = await stages.OpenCheckpointWriterAsync(ticket, default);
                ReportCheckpoint("writer-open");
                if (partialBlock)
                {
                    var first = new byte[StorageStageFileReceipt.MaximumBlockBytes];
                    Array.Fill(first, (byte)0x31);
                    if (args[2] == "checkpoint-partial-next") await checkpoint.WriteBlockAsync(first, default);
                    if (args[2] == "checkpoint-partial-next") ReportCheckpoint("first-block-written");
                    var block = args[2] == "checkpoint-partial-next" ? new byte[first.Length] : first;
                    if (args[2] == "checkpoint-partial-next") Array.Fill(block, (byte)0x52);
                    // Controlled I/O fault in this owned test process only. Use
                    // the real CreateNew descriptor, never reopen by pathname.
                    // This is not a claim of interrupting the OS WriteAsync call.
                    var descriptor = typeof(StorageStageCheckpointWriter).GetField("_file",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(checkpoint) as FileStream
                        ?? throw new InvalidOperationException("Owned checkpoint descriptor is unavailable.");
                    pause.AfterCommitted = async () =>
                    {
                        ReportCheckpoint("plan-committed");
                        await using PaymentContext observer = args[0] == "sqlite"
                            ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                                .UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False").Options)
                            : CreatePostgres(name);
                        var current = await observer.StorageCapacityReservations.AsNoTracking().SingleAsync();
                        if (!StorageStageFileReceipt.TryParse(current.StageFileReceiptJson, out var planned)
                            || planned!.PlannedBytes != upper || planned.FileIdentity != StorageStageFileIdentity.Observe(descriptor.SafeFileHandle)
                            || descriptor.Length != planned.PreviousBytes || descriptor.Position != planned.PreviousBytes)
                            throw new InvalidOperationException("Partial fault requires the original committed boundary.");
                        ReportCheckpoint("plan-observed");
                        await descriptor.WriteAsync(block.AsMemory(0, block.Length / 2));
                        await descriptor.FlushAsync();
                        descriptor.Flush(flushToDisk: true);
                        ReportCheckpoint("partial-bytes-flushed");
                        await WriteCheckpointReadyAsync(root, ticket);
                        await Task.Delay(Timeout.Infinite);
                    };
                    await checkpoint.WriteBlockAsync(block, default);
                    throw new InvalidOperationException("Partial checkpoint unexpectedly passed its fault boundary.");
                }
                if (args[2] == "checkpoint-empty")
                {
                    await WriteCheckpointReadyAsync(root, ticket);
                    await Task.Delay(Timeout.Infinite);
                }
                if (args[2] == "checkpoint-planned")
                    pause.AfterCommitted = async () => { await WriteCheckpointReadyAsync(root, ticket); await Task.Delay(Timeout.Infinite); };
                await checkpoint.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                await checkpoint.CompleteAsync(default);
                await WriteCheckpointReadyAsync(root, ticket);
                await Task.Delay(Timeout.Infinite);
                return;
            }
            if (args[2] != "missing")
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(args[2] == "complete" ? new byte[] { 1, 2, 3, 4 } : new byte[] { 1, 2 });
            }
            using var process = Process.GetCurrentProcess();
            var readyTemporary = Path.Combine(root, "writer-ready.pending");
            await File.WriteAllTextAsync(readyTemporary, JsonSerializer.Serialize(new
            {
                ProcessId = process.Id,
                StartedUtc = process.StartTime.ToUniversalTime().ToString("O"),
                ProcessIdentity = OwnedProcessIdentity.Capture(process),
                Ticket = ticket
            }));
            File.Move(readyTemporary, Path.Combine(root, "writer-ready.json"), overwrite: false);
            await Task.Delay(Timeout.Infinite);
        }
        finally { await ticket.CloseWriterAsync(); }
        void ReportCheckpoint(string phase)
        {
            if (partialBlock) Console.WriteLine($"PP_CHECKPOINT_PROBE {phase} {checkpointClock.ElapsedMilliseconds}");
        }
    }

    private static async Task WriteCheckpointReadyAsync(string root, StorageStageCapacityTicket ticket, object? metrics = null)
    {
        using var process = Process.GetCurrentProcess();
        var temporary = Path.Combine(root, "writer-ready.pending");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            ProcessId = process.Id,
            StartedUtc = process.StartTime.ToUniversalTime().ToString("O"),
            ProcessIdentity = OwnedProcessIdentity.Capture(process),
            Ticket = ticket,
            Metrics = metrics
        }));
        File.Move(temporary, Path.Combine(root, "writer-ready.json"), overwrite: false);
    }

    // Test-only decorator: forwards every real service operation and observes
    // the actual coalescing facade's writer before the original source is read.
    private sealed class PartialOriginalStages(StorageStageCapacityService inner,
        Action<StorageStageCapacityTicket, StorageStageCheckpointWriter> prepare, Action<Stream>? observeFacade = null) : IStorageStageCapacityService
    {
        public Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
            Func<Stream, StorageStageFileReceipt, CancellationToken, Task> write, CancellationToken ct)
            => inner.WriteCheckpointedFileAsync(ticket, (output, initial, token) =>
            {
                var field = output.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SingleOrDefault(f => f.FieldType == typeof(StorageStageCheckpointWriter));
                var writer = field?.GetValue(output) as StorageStageCheckpointWriter
                    ?? throw new InvalidOperationException("Actual original-read checkpoint writer is unavailable.");
                observeFacade?.Invoke(output);
                prepare(ticket, writer);
                return write(output, initial, token);
            }, ct);
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(string identity, string path, long size, CancellationToken ct)
            => inner.ReserveAsync(identity, path, size, ct);
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(string identity, string path, long size, string alias, CancellationToken ct)
            => inner.ReserveCompatibilityAsync(identity, path, size, alias, ct);
        public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket, long size, CancellationToken ct)
            => inner.ConfirmPhysicalSizeAsync(ticket, size, ct);
        public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct) => inner.SettlePhysicalAsync(ticket, ct);
        public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct) => inner.ReleaseAsync(ticket, ct);
        public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit, TimeSpan age, CancellationToken ct, string? afterKey = null)
            => inner.ReconcilePendingAsync(apply, limit, age, ct, afterKey);
    }

    private sealed class OriginalSource(byte[] bytes, Func<Task> beforeRead) : MemoryStream(bytes)
    {
        private bool _started;
        internal long BytesRead;
        internal int ReadCalls;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started) { _started = true; await beforeRead(); }
            var count = await base.ReadAsync(buffer, cancellationToken);
            ReadCalls++;
            BytesRead += count;
            return count;
        }
    }
    private sealed class EvidenceFileReader(string path, long size, string sha256) : IProductStorageReader
    {
        internal int Opens;
        internal CountedEvidenceFile? Content;
        public Task<ProductStorageReadResult> OpenReadAsync(string source, long id, string name, string contentType, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++Opens != 1) throw new InvalidOperationException("Owned evidence source was opened more than once.");
            Content = new CountedEvidenceFile(path);
            return Task.FromResult(new ProductStorageReadResult(ProductStorageReadStatus.Available,
                new StorageObjectReadResult(Guid.NewGuid(), 1, contentType, name, size, Content, "owned-file", sha256)));
        }
    }
    private sealed class CountedEvidenceFile(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)
    {
        internal long BytesRead;
        internal int ReadCalls;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await base.ReadAsync(buffer, ct);
            ReadCalls++;
            BytesRead += count;
            return count;
        }
    }
    private sealed class CheckpointPause : DbTransactionInterceptor
    {
        internal int Commits;
        public Func<Task>? BeforeStarting;
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var pause = BeforeStarting;
            BeforeStarting = null;
            if (pause is not null) await pause();
            return result;
        }
        public Func<Task>? AfterCommitted;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            var pause = AfterCommitted;
            AfterCommitted = null;
            if (pause is not null) await pause();
        }
    }

    private sealed class CheckpointQueryCounts : DbCommandInterceptor
    {
        internal int Reservations, Copies, Total;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Total++;
            if (command.CommandText.Contains("storage-stage-pending-reservation-budget", StringComparison.Ordinal)) Reservations++;
            if (command.CommandText.Contains("storage-capacity-pending-budget", StringComparison.Ordinal)) Copies++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { Total++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<object> result, CancellationToken ct = default)
        { Total++; return ValueTask.FromResult(result); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessIo
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters
    // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-io_counters
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out ProcessIo counters);
    private static ProcessIo? ReadProcessIo()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var process = Process.GetCurrentProcess();
        return GetProcessIoCounters(process.Handle, out var counters) ? counters : null;
    }
    private static ulong? Difference(ulong? previous, ulong? current)
        => previous.HasValue && current.HasValue && current.Value >= previous.Value ? current.Value - previous.Value : null;
    private static string ValidateRoot(string input)
    {
        var root = Path.GetFullPath(input);
        var name = Path.GetFileName(root);
        const string prefix = "pp_stage_fence_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
            || !string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || (File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new ArgumentException("Probe root is not an owned temporary directory.");
        return root;
    }

    private static void RequireMailIsolation()
    {
        if (Environment.GetEnvironmentVariable("Email__Enabled") != "false"
            || Environment.GetEnvironmentVariable("BackgroundServices__Enabled") != "false")
            throw new InvalidOperationException("Probe mail isolation is required.");
    }

    private static PostgreSqlPaymentContext CreatePostgres(string name, params IInterceptor[] interceptors)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"))
        { Database = name, Pooling = false };
        if (settings.Host is not ("127.0.0.1" or "::1"))
            throw new InvalidOperationException("Probe PostgreSQL must use the owned loopback test server.");
        return new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).AddInterceptors(interceptors).Options,
            useObjectAuditModel: false);
    }

    private sealed class ProbeOptions : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue { get; } = new()
        { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 };
        public StorageOrchestrationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class ProbeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.StorageStageWriterProbe";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
