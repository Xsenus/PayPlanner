using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LocalStorageCapacityProbeTests
{
    [Fact]
    public void ActualHostVolumeGroupsExistingAndUncreatedSubfoldersWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-capacity-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = LocalStorageCapacityProbe.Observe(root, "one", 0);
            var second = LocalStorageCapacityProbe.Observe(Path.Combine(root, "not-created", "child"), "two", 10);
            Assert.NotNull(first.AvailableBytes); Assert.Matches("^[a-f0-9]{64}$", first.GroupId);
            Assert.Equal(first.GroupId, second.GroupId); Assert.True(second.AvailableBytes >= 0);
            Assert.Equal(10, second.MinimumFreeBytes); Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("negativeReserve")]
    [InlineData("fileInsteadOfDirectory")]
    public void InvalidProbeReturnsUnknownWithoutInventingCapacity(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-capacity-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = scenario == "relative" ? "relative/path" : root;
            if (scenario == "fileInsteadOfDirectory") { path = Path.Combine(root, "file"); File.WriteAllBytes(path, [1]); }
            var report = LocalStorageCapacityProbe.Observe(path, "local", scenario == "negativeReserve" ? -1 : 0);
            Assert.Null(report.AvailableBytes); Assert.Equal("", report.GroupId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxMountParserUsesLongestBoundaryAndBindDeviceIdentity()
    {
        const string table = "1 0 8:1 / / rw - ext4 /dev/root rw\n2 1 8:2 / /data rw - ext4 /dev/data rw\n3 1 8:2 /sub /bind rw - ext4 /dev/data rw\n4 1 8:3 / /data/deep rw - ext4 /dev/deep rw\n";
        Assert.Equal("8:3", LocalStorageCapacityProbe.SelectLinuxMount(table, "/data/deep/file")!.Value.Device);
        Assert.Equal("8:1", LocalStorageCapacityProbe.SelectLinuxMount(table, "/database/file")!.Value.Device);
        Assert.Equal(LocalStorageCapacityProbe.SelectLinuxMount(table, "/data/file")!.Value.Device,
            LocalStorageCapacityProbe.SelectLinuxMount(table, "/bind/file")!.Value.Device);
    }

    [Fact]
    public void LinuxMountParserDecodesKernelEscapesWithoutTurningLiteralEscapeIntoSpace()
    {
        const string table = "1 0 8:1 / / rw - ext4 /dev/root rw\n2 1 8:2 / /space\\040name rw - ext4 /dev/data rw\n3 1 8:3 / /literal\\134040 rw - ext4 /dev/literal rw\n";
        Assert.Equal("8:2", LocalStorageCapacityProbe.SelectLinuxMount(table, "/space name/file")!.Value.Device);
        Assert.Equal("8:3", LocalStorageCapacityProbe.SelectLinuxMount(table, "/literal\\040/file")!.Value.Device);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("ambiguous")]
    [InlineData("relative")]
    [InlineData("oversized")]
    public void LinuxMountParserRefusesUncertainMetadata(string scenario)
    {
        const string mount = "1 0 8:1 / / rw - ext4 /dev/root rw\n";
        var table = scenario switch
        {
            "malformed" => "broken\n",
            "ambiguous" => mount + mount,
            "oversized" => new string('a', 4 * 1024 * 1024 + 1),
            _ => mount
        };
        Assert.Null(LocalStorageCapacityProbe.SelectLinuxMount(table, scenario == "relative" ? "relative" : "/file"));
    }
}
