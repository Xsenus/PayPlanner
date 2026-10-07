using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "changedSecond")]
    [InlineData(true, "changedSecond")]
    [InlineData(false, "secondFailure")]
    [InlineData(true, "secondFailure")]
    [InlineData(false, "lostAck")]
    [InlineData(true, "lostAck")]
    [InlineData(false, "forgedReceipt")]
    [InlineData(true, "forgedReceipt")]
    [InlineData(false, "foreignGeneration")]
    [InlineData(true, "foreignGeneration")]
    [InlineData(false, "deletedObject")]
    [InlineData(true, "deletedObject")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "staleLease")]
    [InlineData(true, "staleLease")]
    [InlineData(false, "disabledCapacity")]
    [InlineData(true, "disabledCapacity")]
    public async Task ChatOriginalWriterKeepsAllOriginalsSeparateFromMessagePublication(bool postgres, string scenario)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("chat-original-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var request = CaptureSnapshot(thread.Id, author.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root.Path,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            ProductWriteEnabledClasses = ["chat-attachment"],
            UploadCapacityChecksEnabled = scenario != "disabledCapacity",
            UploadMinimumFreeBytes = 0
        };
        var monitor = new ChatOriginalOptions(settings);
        var providers = new StorageProviderRegistry([new LocalStorageProvider(environment, config)]);
        var objects = new StorageObjectService(f.Db, new StoragePolicyService(monitor, providers), providers, monitor);
        var interceptor = new ChatOriginalStorage(objects);
        interceptor.AfterWrite = async (write, call) =>
        {
            if (scenario == "lostAck" && call == 1) return write with { ObjectId = Guid.Empty };
            if (scenario == "forgedReceipt") return write with { ObjectId = Guid.NewGuid() };
            if (scenario == "deletedObject")
                await f.Db.StorageObjects.Where(item => item.Id == write.ObjectId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsDeleted, true));
            if (scenario == "revoked")
                await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == author.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
            if (scenario == "staleLease")
            {
                var successor = Guid.NewGuid();
                await f.Db.InternalChatSendAttempts.Where(item => item.ThreadId == thread.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor));
            }
            if (scenario == "foreignGeneration")
            {
                var foreign = new StorageObject
                {
                    DataClass = "chat-attachment",
                    OwnerType = "chat-attachment",
                    OwnerId = "unrelated",
                    LogicalName = request.Files[0].FileName,
                    ContentType = request.Files[0].ContentType,
                    CurrentGeneration = 1,
                    Version = 1
                };
                var generation = new StorageObjectGeneration
                {
                    StorageObject = foreign,
                    Generation = 1,
                    SizeBytes = 3,
                    Sha256 = request.Files[0].Sha256,
                    CreatedByUserId = author.Id,
                    PolicyVersion = "local-v1"
                };
                generation.Replicas.Add(new StorageReplica
                {
                    ProviderKey = "local",
                    Locator = "synthetic-unrelated",
                    SizeBytes = 3,
                    Sha256 = request.Files[0].Sha256,
                    Status = StorageReplicaStatuses.Available,
                    LastVerifiedAt = DateTime.UtcNow
                });
                f.Db.StorageObjectGenerations.Add(generation);
                await f.Db.SaveChangesAsync();
                await f.Db.StorageOperations.Where(item => item.StorageObjectId == write.ObjectId && item.OperationType == "commit")
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.StorageObjectGenerationId, generation.Id));
            }
            return write;
        };
        interceptor.FailCall = scenario == "secondFailure" ? 2 : null;
        var writer = new InternalChatOriginalWriter(f.Db, interceptor,
            new StorageStageCapacityService(f.Db, providers, monitor, environment, config), environment, config, monitor);
        var result = await writer.WriteAsync(request, ChatOriginalInputs(request, scenario == "changedSecond"), default);
        if (scenario == "success")
        {
            Assert.Equal("accepted", result.Status);
            Assert.True(result.Accepted);
            var acceptedAt = await f.Db.InternalChatSendAttempts.Select(item => item.AcceptedAt).SingleAsync();
            Assert.NotNull(acceptedAt);
            Assert.Equal(2, await f.Db.InternalChatSendAttemptFiles.CountAsync(item => item.StorageObjectId != null));
            var identities = await f.Db.StorageObjects.Select(item => item.Id).OrderBy(item => item).ToArrayAsync();
            await using var fresh = CaptureFreshDb(f);
            var freshObjects = new StorageObjectService(fresh, new StoragePolicyService(monitor, providers), providers, monitor);
            var freshWriter = new InternalChatOriginalWriter(fresh, freshObjects,
                new StorageStageCapacityService(fresh, providers, monitor, environment, config), environment, config, monitor);
            Assert.True((await freshWriter.WriteAsync(request, ChatOriginalInputs(request), default)).Accepted);
            Assert.Equal(identities, await fresh.StorageObjects.Select(item => item.Id).OrderBy(item => item).ToArrayAsync());
            Assert.Equal(acceptedAt, await fresh.InternalChatSendAttempts.Select(item => item.AcceptedAt).SingleAsync());
            Assert.Equal("conflict", (await freshWriter.WriteAsync(request, ChatOriginalInputs(request, true), default)).Status);
        }
        else
        {
            var expectedStatus = scenario switch
            {
                "changedSecond" or "forgedReceipt" or "foreignGeneration" or "deletedObject" => "conflict",
                "revoked" => "forbidden",
                "staleLease" => "busy",
                _ => "unavailable"
            };
            Assert.Equal(expectedStatus, result.Status);
            Assert.False(result.Accepted);
            Assert.False(await f.Db.InternalChatSendAttempts.AnyAsync(item => item.AcceptedAt != null));
            if (scenario is "changedSecond" or "disabledCapacity")
            {
                Assert.Empty(await f.Db.InternalChatSendAttempts.ToArrayAsync());
                Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
                Assert.Equal(0, interceptor.Calls);
            }
            else
            {
                Assert.Equal(scenario == "secondFailure" ? 2 : 1, interceptor.Calls);
                Assert.Equal(scenario == "foreignGeneration" ? 2 : 1, await f.Db.StorageObjects.CountAsync());
                Assert.Single(await f.Db.InternalChatSendAttempts.ToArrayAsync());
            }
            if (scenario is "secondFailure" or "lostAck")
            {
                var original = Assert.Single(await f.Db.StorageObjects.Select(item => item.Id).ToArrayAsync());
                interceptor.FailCall = null;
                interceptor.AfterWrite = null;
                Assert.True((await writer.WriteAsync(request, ChatOriginalInputs(request), default)).Accepted);
                Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
                Assert.True(await f.Db.StorageObjects.AnyAsync(item => item.Id == original));
            }
        }
        Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await f.Db.InternalChatAttachments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Null(await f.Db.InternalChatThreads.Where(item => item.Id == thread.Id).Select(item => item.LastMessageAt).SingleAsync());
        Assert.False(await f.Db.StorageCapacityReservations.AnyAsync(item => item.IdempotencyKey.StartsWith("stage:")
            && item.Status == StorageCapacityReservationStatuses.Pending));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private static IFormFile[] ChatOriginalInputs(InternalChatSendSnapshot request, bool changeSecond = false)
        => request.Files.Select((file, index) => (IFormFile)new FormFile(new MemoryStream(index == 0
            ? new byte[] { 1, 2, 3 } : changeSecond ? new byte[] { 6, 5, 4 } : new byte[] { 4, 5, 6 }),
            0, 3, "files", file.FileName)
        { Headers = new HeaderDictionary(), ContentType = file.ContentType }).ToArray();

    private sealed class ChatOriginalStorage(IStorageObjectService inner) : IStorageObjectService, IStorageOriginalReadService
    {
        public int Calls { get; private set; }
        public int? FailCall { get; set; }
        public Func<StorageObjectWriteResult, int, Task<StorageObjectWriteResult>>? AfterWrite { get; set; }
        public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken ct)
        {
            Calls++;
            if (Calls == FailCall) return new(Guid.Empty, 0, "failed", 0, 1, "local-v1");
            var result = await inner.WriteAsync(request, ct);
            return AfterWrite is null ? result : await AfterWrite(result, Calls);
        }
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken ct) => inner.OpenReadAsync(objectId, ct);
        public Task<StorageObjectReadResult?> OpenReservedReadAsync(StorageObjectWriteRequest request, CancellationToken ct)
            => ((IStorageOriginalReadService)inner).OpenReservedReadAsync(request, ct);
        public Task<StorageObjectReadResult?> OpenOriginalReadAsync(StorageObjectWriteRequest request, CancellationToken ct)
            => ((IStorageOriginalReadService)inner).OpenOriginalReadAsync(request, ct);
        public Task<bool> DeleteAsync(Guid objectId, int? actor, string? reason, CancellationToken ct) => inner.DeleteAsync(objectId, actor, reason, ct);
    }

    private sealed class ChatOriginalOptions(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class ChatOriginalEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class ChatOriginalRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pp_chat_originals_" + Guid.NewGuid().ToString("N")));
        public ChatOriginalRoot() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var expected = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (System.IO.Path.GetDirectoryName(Path) != expected || !Guid.TryParseExact(System.IO.Path.GetFileName(Path)["pp_chat_originals_".Length..], "N", out _))
                throw new InvalidOperationException("Unexpected owned chat originals root.");
            Directory.Delete(Path, true);
        }
    }
}
