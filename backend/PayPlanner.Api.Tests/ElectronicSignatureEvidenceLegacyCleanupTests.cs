using System.Security.Cryptography;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services;
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
    public async Task LegacyEvidenceFailureDeletesOnlyItsVerifiedArchiveAndPreservesSource(bool large, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var original = large ? RandomNumberGenerator.GetBytes(8 * 1024 * 1024) : "%PDF-1.7"u8.ToArray();
        "%PDF-1.7"u8.CopyTo(original);
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", default);
        var root = Path.Combine(fixture.Root, "storage");
        var sourceError = cancel ? (Exception)new OperationCanceledException("Owned legacy source cancellation.")
            : new IOException("Owned legacy source failure.");
        var reader = new AdmissionEvidenceReader(original, original)
        {
            OnEnd = () =>
            {
                Assert.Single(Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories));
                throw sourceError;
            }
        };
        var runtime = CreateEvidenceAdmissionRuntime(fixture, reader);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = false;
        var error = await Record.ExceptionAsync(() => runtime.Service.CreateEvidencePackageAsync(envelope.PublicId, default));
        Assert.Same(sourceError, error);
        Assert.Empty(Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("original")]
    [InlineData("same-bytes-replacement")]
    [InlineData("changed-bytes")]
    [InlineData("root-replacement")]
    [InlineData("unknown-file")]
    [InlineData("unknown-root")]
    public async Task LegacyEvidenceClosedWriterCleanupRequiresExactOriginalProof(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(fixture.Root, "storage");
        var path = Path.Combine(root, ".exports", "owned-proof.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = "Owned original archive bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var rootIdentity = StorageStageRootIdentity.Observe(root);
        Assert.NotNull(rootIdentity);
        string? fileIdentity;
        await using (var file = File.OpenRead(path)) fileIdentity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
        Assert.NotNull(fileIdentity);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        string? retained = null;
        if (change == "same-bytes-replacement")
        {
            retained = path + ".owned-original";
            File.Move(path, retained);
            await File.WriteAllBytesAsync(path, bytes);
        }
        if (change == "changed-bytes") await File.AppendAllTextAsync(path, "Changed");
        if (change == "root-replacement")
        {
            var heldRoot = Path.Combine(fixture.Root, "owned-original-storage");
            Assert.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Assert.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(heldRoot));
            var relative = Path.GetRelativePath(root, path);
            Directory.Move(root, heldRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(Path.Combine(heldRoot, relative), path);
            await using var file = File.OpenRead(path);
            Assert.Equal(fileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        }
        var expectedBytes = await File.ReadAllBytesAsync(path);
        var method = typeof(ElectronicSignatureService).GetMethod("TryCleanupSignatureTemporaryFileAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var deleted = await (Task<bool>)method.Invoke(null, [path, root,
            change == "unknown-root" ? null : rootIdentity, change == "unknown-file" ? null : fileIdentity,
            bytes.LongLength, sha256])!;
        Assert.Equal(change == "original", deleted);
        if (deleted) Assert.False(File.Exists(path));
        else Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(path));
        if (retained is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(retained));
    }
}
