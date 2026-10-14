using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Fact]
    public async Task StoppedStageWaitDoesNotAssumeMarkerReleaseMeansStageRelease()
    {
        using var fixture = new StageHandleFixture();
        await using var writer = new FileStream(fixture.Stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var identity = Assert.IsType<string>(StorageStageFileIdentity.Observe(writer.SafeFileHandle));
        var waiting = WaitForStoppedStageHandlesAsync(fixture.Marker, fixture.Stage, identity);
        Assert.False(waiting.IsCompleted);
        await writer.DisposeAsync();
        await waiting;
    }

    [Fact]
    public async Task StoppedStageWaitStillRequiresExclusiveMarkerAccess()
    {
        using var fixture = new StageHandleFixture();
        await using var stage = new FileStream(fixture.Stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var identity = Assert.IsType<string>(StorageStageFileIdentity.Observe(stage.SafeFileHandle));
        await stage.DisposeAsync();
        await using var marker = new FileStream(fixture.Marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var waiting = WaitForStoppedStageHandlesAsync(fixture.Marker, fixture.Stage, identity);
        Assert.False(waiting.IsCompleted);
        await marker.DisposeAsync();
        await waiting;
    }

    [Fact]
    public async Task StoppedStageWaitRejectsReplacementFileIdentity()
    {
        using var fixture = new StageHandleFixture();
        await using var original = new FileStream(fixture.Stage, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        var identity = Assert.IsType<string>(StorageStageFileIdentity.Observe(original.SafeFileHandle));
        File.Move(fixture.Stage, fixture.Stage + ".original");
        await File.WriteAllBytesAsync(fixture.Stage, new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<Xunit.Sdk.EqualException>(() =>
            WaitForStoppedStageHandlesAsync(fixture.Marker, fixture.Stage, identity));
    }

    private sealed class StageHandleFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pp-stage-handles-" + Guid.NewGuid().ToString("N"));
        public string Stage => Path.Combine(root, "stage.tmp");
        public string Marker => Path.Combine(root, "marker.lock");

        public StageHandleFixture()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Marker, "owned-marker");
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(tempRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Path.GetFileName(fullRoot).StartsWith("pp-stage-handles-", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic stage fixture is outside its temporary root.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
