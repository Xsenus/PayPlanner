using System.Diagnostics;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnedProcessIdentityTests
{
    private const string Boot = "84f19f6e-88a5-4a01-9f81-a176c048b72b";

    [Fact]
    public void CaptureMatchesIndependentObservationOfSameLiveProcess()
    {
        using var current = Process.GetCurrentProcess();
        using var observed = Process.GetProcessById(current.Id);
        Assert.Equal(OwnedProcessIdentity.Capture(current), OwnedProcessIdentity.Capture(observed));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("worker ) (name)")]
    [InlineData("worker\nname")]
    public void LinuxIdentityUsesExactKernelTicksDespiteCommandName(string command)
    {
        Assert.Equal("linux:84f19f6e88a54a019f81a176c048b72b:123:18446744073709551615",
            OwnedProcessIdentity.ParseLinux(123, Boot + "\n", Stat(command, "18446744073709551615")));
    }

    [Fact]
    public void LinuxIdentityDistinguishesBootPidAndSingleKernelTick()
    {
        var identity = OwnedProcessIdentity.ParseLinux(123, Boot, Stat("worker", "42"));
        Assert.NotEqual(identity, OwnedProcessIdentity.ParseLinux(123, "74f19f6e-88a5-4a01-9f81-a176c048b72b", Stat("worker", "42")));
        Assert.NotEqual(identity, OwnedProcessIdentity.ParseLinux(124, Boot, Stat("worker", "42").Replace("123 (", "124 (")));
        Assert.NotEqual(identity, OwnedProcessIdentity.ParseLinux(123, Boot, Stat("worker", "43")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+42")]
    [InlineData("1.0")]
    [InlineData("18446744073709551616")]
    [InlineData("missing")]
    public void LinuxIdentityRejectsInvalidKernelTicks(string ticks)
        => Assert.Throws<InvalidDataException>(() => OwnedProcessIdentity.ParseLinux(123, Boot, Stat("worker", ticks)));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-boot-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void LinuxIdentityRejectsMissingBootWithOtherwiseValidStat(string boot)
        => Assert.Throws<InvalidDataException>(() => OwnedProcessIdentity.ParseLinux(123, boot, Stat("worker", "42")));

    [Theory]
    [InlineData(124)]
    [InlineData(0)]
    [InlineData(-1)]
    public void LinuxIdentityRejectsMismatchedPidWithOtherwiseValidStat(int pid)
        => Assert.Throws<InvalidDataException>(() => OwnedProcessIdentity.ParseLinux(pid, Boot, Stat("worker", "42")));

    [Theory]
    [InlineData("123 (worker) S 1")]
    [InlineData("123 worker S 1")]
    [InlineData("123 (worker S 1")]
    [InlineData("123(worker) S 1")]
    public void LinuxIdentityRejectsIncompleteStat(string stat)
        => Assert.Throws<InvalidDataException>(() => OwnedProcessIdentity.ParseLinux(123, Boot, stat));

    private static string Stat(string command, string startTicks)
        => $"123 ({command}) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 {startTicks} 999 888";
}
