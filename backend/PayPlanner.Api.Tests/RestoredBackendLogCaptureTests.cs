using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace PayPlanner.Api.Tests;

// Exercises the actual receive/assert path without starting a backend or database.
public sealed class RestoredBackendLogCaptureTests
{
    [Fact]
    public async Task EarlyServerErrorCannotBeEvictedByLaterNormalLogs()
    {
        await using var probe = new LogProbe();
        probe.Receive("fail: Microsoft.EntityFrameworkCore.Database.Connection[20004]");
        for (var i = 0; i < 600; i++) probe.Receive($"info: Example.Component[0] Normal line {i}");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(probe.Backend.AssertNoServerErrors);
        Assert.Contains("Microsoft.EntityFrameworkCore.Database.Connection", failure.Message);
    }

    [Fact]
    public async Task FailureSummaryKeepsCategoryButNeverCopiesArbitraryHeaderDetails()
    {
        await using var probe = new LogProbe();
        probe.Receive("fail: Example.Component[17] credential=synthetic-log-capture-secret");
        probe.Receive("Npgsql.NpgsqlException: password=synthetic-log-capture-secret; SELECT synthetic-private-value");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(probe.Backend.AssertNoServerErrors);
        Assert.Contains("Example.Component", failure.Message);
        Assert.Contains("Npgsql.NpgsqlException", failure.Message);
        Assert.DoesNotContain("synthetic-log-capture-secret", failure.Message);
        Assert.DoesNotContain("synthetic-private-value", failure.Message);
    }

    [Fact]
    public async Task UnknownErrorHeaderCannotBecomeSuccessOrRevealItsPayload()
    {
        await using var probe = new LogProbe();
        probe.Receive("fail: credential=synthetic-log-capture-secret");
        for (var i = 0; i < 600; i++) probe.Receive("info: Example.Component[0] Normal line");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(probe.Backend.AssertNoServerErrors);
        Assert.DoesNotContain("synthetic-log-capture-secret", failure.Message);
    }

    [Fact]
    public async Task LargeFailureBurstRemainsFailureWithBoundedSafeSummary()
    {
        await using var probe = new LogProbe();
        for (var i = 0; i < 1000; i++)
            probe.Receive($"fail: Example.Component{i}[17] detail=synthetic-log-capture-secret");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(probe.Backend.AssertNoServerErrors);
        Assert.True(failure.Message.Length <= 8192);
        Assert.DoesNotContain("synthetic-log-capture-secret", failure.Message);
    }

    [Fact]
    public async Task NormalLogHistoryDoesNotCreateAFalseServerFailure()
    {
        await using var probe = new LogProbe();
        for (var i = 0; i < 1000; i++) probe.Receive("info: Example.Component[0] Normal line");
        probe.Backend.AssertNoServerErrors();
    }

    [Fact]
    public async Task SeparateHelpersDoNotShareEarlierFailures()
    {
        await using var failed = new LogProbe();
        await using var clean = new LogProbe();
        failed.Receive("fail: Example.Component[17]");
        Assert.Throws<Xunit.Sdk.TrueException>(failed.Backend.AssertNoServerErrors);
        clean.Backend.AssertNoServerErrors();
    }

    private sealed class LogProbe : IAsyncDisposable
    {
        private static readonly MethodInfo ReceiveMethod = typeof(RestoredBackendProcess)
            .GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public RestoredBackendProcess Backend { get; }

        public LogProbe()
        {
            var constructor = typeof(RestoredBackendProcess).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(Process)], null)!;
            var process = new Process();
            try { Backend = (RestoredBackendProcess)constructor.Invoke([process]); }
            catch { process.Dispose(); throw; }
        }

        public void Receive(string line) => ReceiveMethod.Invoke(Backend, [line]);
        public ValueTask DisposeAsync() => Backend.DisposeAsync();
    }
}