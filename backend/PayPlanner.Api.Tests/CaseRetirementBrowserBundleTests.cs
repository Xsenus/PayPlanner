using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseRetirementBrowserBundleTests
{
    [Theory]
    [InlineData("different-temp", "different-tmp")]
    [InlineData(null, "different-tmp")]
    [InlineData("different-temp", null)]
    public async Task NodeTemporaryDirectoryMatchesTheValidatedDotnetOwner(string? temp, string? tmp)
    {
        await using var owner = new CaseRetirementBrowserBundle();
        using var lease = owner.Acquire();
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var parentTemp = Environment.GetEnvironmentVariable("TEMP");
        var parentTmp = Environment.GetEnvironmentVariable("TMP");
        if (temp is null) start.Environment.Remove("TEMP");
        else start.Environment["TEMP"] = Path.Combine(Path.GetTempPath(), temp);
        if (tmp is null) start.Environment.Remove("TMP");
        else start.Environment["TMP"] = Path.Combine(Path.GetTempPath(), tmp);
        lease.ConfigureNodeTemporaryDirectory(start.Environment);
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("console.log(require('node:path').resolve(require('node:os').tmpdir()))");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Equal(Path.GetDirectoryName(owner.Root), (await stdout).Trim());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
        Assert.Equal(parentTemp, Environment.GetEnvironmentVariable("TEMP"));
        Assert.Equal(parentTmp, Environment.GetEnvironmentVariable("TMP"));
    }

    [Fact]
    public async Task ChangedOwnerCannotRedirectTheNodeTemporaryDirectory()
    {
        var owner = new CaseRetirementBrowserBundle();
        var marker = Path.Combine(owner.Root, "owner.json");
        var original = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker, "foreign-owner");
        var environment = new Dictionary<string, string?> { ["TEMP"] = "original-temp", ["TMP"] = "original-tmp" };
        try
        {
            Assert.Throws<IOException>(() => owner.ConfigureNodeTemporaryDirectory(environment));
            Assert.Equal("original-temp", environment["TEMP"]);
            Assert.Equal("original-tmp", environment["TMP"]);
        }
        finally
        {
            await File.WriteAllTextAsync(marker, original);
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleasedLeaseCannotRedirectTheNodeTemporaryDirectory()
    {
        await using var owner = new CaseRetirementBrowserBundle();
        var lease = owner.Acquire();
        lease.Dispose();
        var environment = new Dictionary<string, string?> { ["TEMP"] = "original-temp", ["TMP"] = "original-tmp" };
        Assert.Throws<ObjectDisposedException>(() => lease.ConfigureNodeTemporaryDirectory(environment));
        Assert.Equal("original-temp", environment["TEMP"]);
        Assert.Equal("original-tmp", environment["TMP"]);
    }

    [Fact]
    public async Task AddressIsExclusiveBetweenFreshRuntimesAndConcurrentAdmissionIsRejected()
    {
        await using var owner = new CaseRetirementBrowserBundle();
        var address = owner.ApiAddress;
        using (var first = owner.Acquire())
        {
            Assert.Throws<InvalidOperationException>(() => owner.Acquire());
            var collision = new TcpListener(IPAddress.Loopback, address.Port) { ExclusiveAddressUse = true };
            try { Assert.Throws<SocketException>(() => collision.Start()); }
            finally { collision.Stop(); }
            first.ReleaseReservationForStart();
            var freshProcessStandIn = new TcpListener(IPAddress.Loopback, address.Port) { ExclusiveAddressUse = true };
            freshProcessStandIn.Start();
            freshProcessStandIn.Stop();
        }
        using var second = owner.Acquire();
        Assert.Equal(address, second.ApiAddress);
        Assert.Equal(owner.Root, second.Root);
    }

    [Fact]
    public async Task SurvivingListenerCannotBeAdoptedOrSilentlyAssignedAnotherPort()
    {
        var owner = new CaseRetirementBrowserBundle();
        var lease = owner.Acquire();
        lease.ReleaseReservationForStart();
        var surviving = new TcpListener(IPAddress.Loopback, owner.ApiAddress.Port) { ExclusiveAddressUse = true };
        surviving.Start();
        try
        {
            Assert.Throws<SocketException>(() => lease.Dispose());
            Assert.Throws<InvalidOperationException>(() => owner.Acquire());
            await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync());
            Assert.True(Directory.Exists(owner.Root));
        }
        finally
        {
            surviving.Stop();
            // This negative fixture made only the marker; do not recursively remove anything unexpected.
            Assert.Single(Directory.GetFileSystemEntries(owner.Root));
            File.Delete(Path.Combine(owner.Root, "owner.json"));
            Directory.Delete(owner.Root);
        }
    }

    [Fact]
    public async Task ChangedOwnerCannotAdmitOrEraseForeignMetadata()
    {
        var owner = new CaseRetirementBrowserBundle();
        var marker = Path.Combine(owner.Root, "owner.json");
        var original = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker, "foreign-owner");
        Assert.Throws<IOException>(() => owner.Acquire());
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync());
        Assert.Equal("foreign-owner", await File.ReadAllTextAsync(marker));
        await File.WriteAllTextAsync(marker, original);
        await owner.DisposeAsync();
        Assert.False(Directory.Exists(owner.Root));
    }

    [Theory]
    [InlineData("build-attempt.json")]
    [InlineData("bundle-use.lock")]
    [InlineData("failure.json")]
    public async Task FailedOrInterruptedOwnerCannotStartAnotherRuntimeOrLoseItsTrace(string evidence)
    {
        var owner = new CaseRetirementBrowserBundle();
        var path = Path.Combine(owner.Root, evidence);
        await File.WriteAllTextAsync(path, "retained-attempt");
        Assert.Throws<IOException>(() => owner.Acquire());
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync());
        Assert.Equal("retained-attempt", await File.ReadAllTextAsync(path));
        File.Delete(path);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task IndependentClassesNeverShareTheirAddressOrArtifactRoot()
    {
        await using var first = new CaseRetirementBrowserBundle();
        await using var second = new CaseRetirementBrowserBundle();
        Assert.NotEqual(first.OwnerId, second.OwnerId);
        Assert.NotEqual(first.Root, second.Root);
        Assert.NotEqual(first.ApiAddress, second.ApiAddress);
    }
}
