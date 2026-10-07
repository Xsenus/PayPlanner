using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceHttpRejectsOriginalFileMovedIntoReplacedRoot(bool capacityEnabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", default);
        var service = capacityEnabled ? CreateEvidenceAdmissionRuntime(fixture).Service : fixture.Service;
        var package = (await service.CreateEvidencePackageAsync(envelope.PublicId, default))!;
        var root = Path.Combine(fixture.Root, "storage");
        var heldRoot = Path.Combine(fixture.Root, "owned-original-storage");
        var relative = Path.GetRelativePath(root, package.FullPath);
        var bytes = await File.ReadAllBytesAsync(package.FullPath);
        string? identity;
        await using (var file = File.OpenRead(package.FullPath)) identity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
        Assert.NotNull(identity);
        Directory.Move(root, heldRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(package.FullPath)!);
        File.Move(Path.Combine(heldRoot, relative), package.FullPath);
        await using (var file = File.OpenRead(package.FullPath)) Assert.Equal(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenEvidencePackageAsync(package, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(package.FullPath));
        Assert.True(File.Exists(Path.Combine(heldRoot, Path.GetRelativePath(root, envelope.SnapshotPath))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EvidenceMvcExecutorDisposesOriginalStreamAfterResponseFailure(bool capacityEnabled, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", default);
        IElectronicSignatureService service = capacityEnabled ? CreateEvidenceAdmissionRuntime(fixture).Service : fixture.Service;
        using var cancellation = new CancellationTokenSource();
        using var body = new FailedEvidenceResponseStream(cancellation, cancel);
        var context = new DefaultHttpContext();
        context.RequestAborted = cancellation.Token;
        context.Response.Body = body;
        var controller = new ElectronicSignaturesController(fixture.Db, service, new ConfigurationBuilder().Build())
        {
            ControllerContext = new() { HttpContext = context }
        };
        var result = Assert.IsType<FileStreamResult>(await controller.DownloadEvidencePackage(envelope.PublicId, default));
        var executor = new FileStreamResultExecutor(NullLoggerFactory.Instance);
        var error = await Record.ExceptionAsync(() => executor.ExecuteAsync(
            new ActionContext(context, new RouteData(), new ActionDescriptor()), result));
        if (cancel) Assert.Null(error);
        else Assert.IsType<IOException>(error);
        Assert.True(body.Writes > 0);
        Assert.False(result.FileStream.CanRead);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.True(File.Exists(envelope.SnapshotPath));
    }

    private sealed class FailedEvidenceResponseStream(CancellationTokenSource cancellation, bool cancel) : MemoryStream
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Writes++;
            if (!cancel) throw new IOException("Owned synthetic response write failure.");
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EvidenceHttpRejectsReplacedPackageWithoutDeletingEitherFile(bool capacityEnabled, bool identicalBytes)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var original = "%PDF-1.7"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", default);
        var service = capacityEnabled ? CreateEvidenceAdmissionRuntime(fixture).Service : fixture.Service;
        var package = (await service.CreateEvidencePackageAsync(envelope.PublicId, default))!;
        var bytes = await File.ReadAllBytesAsync(package.FullPath);
        var heldPath = package.FullPath + ".owned-original";
        File.Move(package.FullPath, heldPath);
        var replacement = identicalBytes ? bytes : "Owned unrelated replacement"u8.ToArray();
        await File.WriteAllBytesAsync(package.FullPath, replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenEvidencePackageAsync(package, default));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(package.FullPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(heldPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(envelope.SnapshotPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceHttpCancelledVerificationRetainsUnhandedPackage(bool capacityEnabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream("%PDF-1.7"u8.ToArray()), "invoice.pdf", default);
        var service = capacityEnabled ? CreateEvidenceAdmissionRuntime(fixture).Service : fixture.Service;
        var package = (await service.CreateEvidencePackageAsync(envelope.PublicId, default))!;
        var bytes = await File.ReadAllBytesAsync(package.FullPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenEvidencePackageAsync(package, cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(package.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceHttpOwnsVerifiedReadonlyStreamUntilResponseDisposal(bool capacityEnabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var original = "%PDF-1.7"u8.ToArray();
        var envelope = await fixture.Service.PrepareAsync(fixture.Request(), new MemoryStream(original), "invoice.pdf", default);
        IElectronicSignatureService service = capacityEnabled ? CreateEvidenceAdmissionRuntime(fixture).Service : fixture.Service;
        var controller = new ElectronicSignaturesController(fixture.Db, service, new ConfigurationBuilder().Build())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        var result = Assert.IsType<FileStreamResult>(await controller.DownloadEvidencePackage(envelope.PublicId, default));
        Assert.Equal("application/zip", result.ContentType);
        Assert.False(result.EnableRangeProcessing);
        Assert.False(result.FileStream.CanWrite);
        Assert.Contains("invoice-101-v1.zip", result.FileDownloadName);
        using (var zip = new ZipArchive(result.FileStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            using var source = zip.GetEntry("original/document.pdf")!.Open();
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy);
            Assert.Equal(original, copy.ToArray());
        }
        await result.FileStream.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.True(File.Exists(envelope.SnapshotPath));
    }

    private static Task<FileStream> OpenEvidencePackageAsync(ElectronicSignatureEvidencePackage package, CancellationToken ct)
        => (Task<FileStream>)typeof(ElectronicSignatureEvidencePackage)
            .GetMethod("OpenReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(package, [ct])!;
}
