using Microsoft.EntityFrameworkCore;
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
    [InlineData(false, "changed-original")]
    [InlineData(true, "changed-original")]
    [InlineData(false, "root")]
    public async Task PdfPublicationRejectsChangedOriginalBeforeFinalMove(bool capacityEnabled, string change)
    {
        var query = new PdfReplayQueryBoundary();
        await using var fixture = await Fixture.CreateAsync(interceptors: [query]);
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var runtime = CreateEvidenceAdmissionRuntime(fixture);
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = capacityEnabled;
        var root = Path.Combine(fixture.Root, "storage");
        var bytes = "%PDF-1.7 original before final move"u8.ToArray();
        PdfCleanupReplacement? replacement = null;
        query.Action = () =>
        {
            var path = Assert.Single(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            if (change == "changed-original")
            {
                string? identity;
                using (var file = File.OpenRead(path)) identity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
                Assert.NotNull(identity);
                File.AppendAllText(path, "Changed same native file");
                using (var file = File.OpenRead(path)) Assert.Equal(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
                replacement = new(path, null, File.ReadAllBytes(path));
            }
            else replacement = ReplacePdfCleanupCandidate(root, path, change, bytes);
        };
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", default));
        Assert.Null(query.Action);
        Assert.NotNull(replacement);
        // A successful registration with stale source identity/bytes is the
        // substantive baseline failure, even when replacement bytes match.
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Empty(await fixture.Db.ElectronicSignatureSigners.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        await AssertPdfCleanupReplacementPreservedAsync(replacement!, bytes);
        Assert.Empty(Directory.EnumerateFiles(root, "original.pdf", SearchOption.AllDirectories));
    }
}
