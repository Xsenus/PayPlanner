using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Win32.SafeHandles;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false, "same-bytes")]
    [InlineData(true, "same-bytes")]
    [InlineData(false, "changed-bytes")]
    [InlineData(true, "changed-bytes")]
    [InlineData(false, "root")]
    public async Task ExactPdfReplayCleanupPreservesReplacedFileOrRoot(bool capacityEnabled, string change)
    {
        var query = new PdfReplayQueryBoundary();
        await using var fixture = await Fixture.CreateAsync(interceptors: [query]);
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = capacityEnabled;
        var bytes = "%PDF-1.7 owned replay cleanup"u8.ToArray();
        var original = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        var root = Path.Combine(fixture.Root, "storage");
        PdfCleanupReplacement? replacement = null;
        query.Action = () => replacement = ReplacePdfCleanupCandidate(root,
            Assert.Single(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories)), change, bytes);
        var replay = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default);
        Assert.Equal(original.Id, replay.Id);
        Assert.Null(query.Action);
        Assert.NotNull(replacement);
        await AssertPdfCleanupReplacementPreservedAsync(replacement!, bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original.SnapshotPath));
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "same-bytes")]
    [InlineData(true, "same-bytes")]
    [InlineData(false, "changed-bytes")]
    [InlineData(true, "changed-bytes")]
    [InlineData(false, "root")]
    public async Task ConfirmedPdfRollbackCleanupPreservesReplacedFileOrRoot(bool capacityEnabled, string change)
    {
        var save = new PdfRollbackSaveBoundary();
        await using var fixture = await Fixture.CreateAsync(interceptors: [save]);
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = capacityEnabled;
        var bytes = "%PDF-1.7 owned rollback cleanup"u8.ToArray();
        var root = Path.Combine(fixture.Root, "storage");
        PdfCleanupReplacement? replacement = null;
        var sourceError = new IOException("Owned domain registration failure after candidate replacement.");
        var liveReplacementBlocked = false;
        save.Action = () =>
        {
            var path = Assert.Single(Directory.EnumerateFiles(root, "original.pdf", SearchOption.AllDirectories));
            var rootIdentity = StorageStageRootIdentity.Observe(root);
            try { replacement = ReplacePdfCleanupCandidate(root, path, change, bytes); }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                liveReplacementBlocked = true;
                Assert.Equal(rootIdentity, StorageStageRootIdentity.Observe(root));
                using var file = OpenPdfCleanupObservation(path);
                using var copy = new MemoryStream();
                file.CopyTo(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
            throw sourceError;
        };
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default));
        Assert.Same(sourceError, error);
        Assert.Null(save.Action);
        if (OperatingSystem.IsWindows()) Assert.True(liveReplacementBlocked);
        if (liveReplacementBlocked)
        {
            Assert.Empty(Directory.EnumerateFiles(root, "original.pdf", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        else
        {
            Assert.NotNull(replacement);
            await AssertPdfCleanupReplacementPreservedAsync(replacement!, bytes);
        }
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureSigners.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    private sealed record PdfCleanupReplacement(string Path, string? RetainedOriginalPath, byte[] ExpectedBytes);

    private static FileStream OpenPdfCleanupObservation(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), FileAccess.Read);

        // Observe the native object without requesting a second managed flock.
        // The production snapshot keeps its exclusive lock during registration;
        // Linux namespace replacement remains possible while that fd is held.
        var descriptor = OpenPdfCleanupObservationLinux(path, 0x20000 | 0x80000 | 0x800); // O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not open the native PDF observation (errno {error}).");
        }
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    private static extern int OpenPdfCleanupObservationLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static PdfCleanupReplacement ReplacePdfCleanupCandidate(string root, string path, string change, byte[] bytes)
    {
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(path));
        string? identity;
        using (var file = OpenPdfCleanupObservation(path)) identity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
        Assert.NotNull(identity);
        if (change == "root")
        {
            var rootIdentity = StorageStageRootIdentity.Observe(root);
            var heldRoot = root + ".owned-original-root";
            Directory.Move(root, heldRoot);
            Directory.CreateDirectory(root);
            // Restore exact children, preserving both the prior envelope and
            // candidate native file. Only the root directory has been replaced.
            foreach (var child in Directory.EnumerateFileSystemEntries(heldRoot))
            {
                var destination = Path.Combine(root, Path.GetFileName(child));
                if (Directory.Exists(child)) Directory.Move(child, destination);
                else File.Move(child, destination);
            }
            Assert.NotEqual(rootIdentity, StorageStageRootIdentity.Observe(root));
            using var file = OpenPdfCleanupObservation(path);
            Assert.Equal(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
            return new(path, null, bytes);
        }
        var retained = path + ".owned-original";
        File.Move(path, retained);
        var replacement = change == "same-bytes" ? bytes : "%PDF-1.7 foreign changed contents"u8.ToArray();
        File.WriteAllBytes(path, replacement);
        using (var file = OpenPdfCleanupObservation(path)) Assert.NotEqual(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        return new(path, retained, replacement);
    }

    private static async Task AssertPdfCleanupReplacementPreservedAsync(PdfCleanupReplacement replacement, byte[] original)
    {
        Assert.Equal(replacement.ExpectedBytes, await File.ReadAllBytesAsync(replacement.Path));
        if (replacement.RetainedOriginalPath is not null)
            Assert.Equal(original, await File.ReadAllBytesAsync(replacement.RetainedOriginalPath));
    }

    private sealed class PdfReplayQueryBoundary : DbCommandInterceptor
    {
        internal Action? Action { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Action is { } action && command.CommandText.Contains("ElectronicSignatureEnvelopes", StringComparison.Ordinal)
                && command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal))
            {
                Action = null;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PdfRollbackSaveBoundary : SaveChangesInterceptor
    {
        internal Action? Action { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Action is { } action && eventData.Context!.ChangeTracker.Entries<ElectronicSignatureEnvelope>()
                .Any(entry => entry.State == EntityState.Added))
            {
                Action = null;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }
}
