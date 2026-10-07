using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Controllers;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnedTaskReadDiagnosticsTests
{
    [Theory]
    [InlineData(nameof(TasksController.GetTask))]
    [InlineData(nameof(TasksController.GetSettings))]
    [InlineData(nameof(TasksController.GetFileUploads))]
    [InlineData(nameof(TasksController.UploadFile))]
    public void MeasuresOnlyNumericalStateWithoutRenderingPrivateValues(string action)
    {
        var accessor = new HttpContextAccessor { HttpContext = Context(action) };
        accessor.HttpContext.Request.QueryString = new QueryString("?private-secret=not-for-output");
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.CommandCategory, 20101, "elapsed", "2.5");
        Emit(provider, OwnedTaskReadDiagnostics.CommandCategory, 20101, "elapsed", "1,000.25");
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1250.0);
        var line = Assert.Single(lines);
        var result = Assert.IsType<OwnedTaskReadDiagnostics.Measurement>(OwnedTaskReadDiagnostics.ReadMeasurement(line));
        Assert.Equal(action, result.Action);
        Assert.Equal(2, result.Commands);
        Assert.Equal(1002.75, result.CommandMilliseconds);
        Assert.Equal(1250.0, result.ActionMilliseconds);
        Assert.DoesNotContain("private", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", line);
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive(line);
        recorder.Receive("fail: Example.Service[0]");
        Assert.Contains("Server fail log entries: 1.", recorder.FailureSummary());
    }

    [Fact]
    public void ConcurrentRequestsDoNotShareCostsAndUnrelatedActionsAreIgnored()
    {
        var accessor = new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) };
        var first = accessor.HttpContext;
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.CommandCategory, 20101, "elapsed", "3");
        accessor.HttpContext = Context(nameof(TasksController.GetSettings));
        Emit(provider, OwnedTaskReadDiagnostics.CommandCategory, 20101, "elapsed", "7");
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 10.0);
        accessor.HttpContext = first;
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 12.0);
        accessor.HttpContext = Context(nameof(TasksController.GetTasks));
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1.0);
        accessor.HttpContext = null;
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1.0);
        Assert.Equal(new[] { 7.0, 3.0 }, lines.Select(line => OwnedTaskReadDiagnostics.ReadMeasurement(line)!.CommandMilliseconds));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("3600001")]
    [InlineData("private-input")]
    public void InvalidDurationsCannotBecomeMeasurements(string elapsed)
    {
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) }, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.CommandCategory, 20101, "elapsed", elapsed);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", elapsed);
        Assert.Empty(lines);
    }

    [Fact]
    public void UnknownEventsAndOversizedPrivatePayloadsAreRejectedAndOutputIsBounded()
    {
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) }, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 102, "ElapsedMilliseconds", 1.0);
        Emit(provider, "private.category", 105, "ElapsedMilliseconds", 1.0);
        Assert.Empty(lines);
        for (var i = 0; i < 100; i++) Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1.0);
        Assert.Equal(64, lines.Count);
        Assert.Null(OwnedTaskReadDiagnostics.ReadMeasurement(OwnedTaskReadDiagnostics.Prefix + new string('x', 513)));
        Assert.Null(OwnedTaskReadDiagnostics.ReadMeasurement(OwnedTaskReadDiagnostics.Prefix + "{"));
        Assert.Null(OwnedTaskReadDiagnostics.ReadMeasurement(OwnedTaskReadDiagnostics.Prefix + JsonSerializer.Serialize(
            new OwnedTaskReadDiagnostics.Measurement("private-action", 1, 1, 1, DateTime.UtcNow.ToString("o")))));
    }

    [Theory]
    [InlineData(nameof(TasksController.GetTask))]
    [InlineData(nameof(TasksController.GetSettings))]
    [InlineData(nameof(TasksController.GetFileUploads))]
    [InlineData(nameof(TasksController.UploadFile))]
    public void RequestPhasesUseHostingDurationAndMonotonicMvcBoundaryWithoutPrivateState(string action)
    {
        var clock = new NumericalClock { Timestamp = 100 };
        var accessor = new HttpContextAccessor { HttpContext = Context(action) };
        accessor.HttpContext.Request.QueryString = new QueryString("?private-secret=not-for-output");
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add, clock);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 100.0);
        Assert.Single(lines); // Existing MVC record remains independently available.
        clock.Timestamp = 150;
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 200.0);
        var result = Assert.Single(lines.Select(OwnedTaskReadDiagnostics.ReadRequestMeasurement).OfType<OwnedTaskReadDiagnostics.RequestMeasurement>());
        Assert.Equal(action, result.Action);
        Assert.Equal(200, result.RequestMilliseconds);
        Assert.Equal(100, result.MvcMilliseconds);
        Assert.Equal(50, result.BeforeMvcMilliseconds);
        Assert.Equal(50, result.AfterMvcMilliseconds);
        Assert.All(lines, line => Assert.DoesNotContain("private", line, StringComparison.OrdinalIgnoreCase));
        Assert.All(lines, line => Assert.DoesNotContain("SELECT", line));
        // Repeated hosting notifications cannot duplicate the same completed request.
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 200.0);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void HostingTimingRequiresTheActualAllowedMvcCompletion()
    {
        var accessor = new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) };
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 100.0);
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 1, "ElapsedMilliseconds", 100.0);
        accessor.HttpContext = Context(nameof(TasksController.GetTasks));
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 100.0);
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 100.0);
        accessor.HttpContext = null;
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 100.0);
        Assert.Empty(lines);
    }

    [Fact]
    public void InterleavedHostingCompletionsKeepTheirOwnMvcBoundary()
    {
        var clock = new NumericalClock { Timestamp = 100 };
        var accessor = new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) };
        var first = accessor.HttpContext;
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add, clock);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 100.0);
        accessor.HttpContext = Context(nameof(TasksController.GetSettings));
        clock.Timestamp = 120;
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 30.0);
        clock.Timestamp = 170;
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 100.0);
        accessor.HttpContext = first;
        clock.Timestamp = 180;
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 200.0);
        var requests = lines.Select(OwnedTaskReadDiagnostics.ReadRequestMeasurement).OfType<OwnedTaskReadDiagnostics.RequestMeasurement>().ToArray();
        Assert.Equal(new[] { nameof(TasksController.GetSettings), nameof(TasksController.GetTask) }, requests.Select(item => item.Action));
        Assert.Equal(new[] { 50.0, 80.0 }, requests.Select(item => item.AfterMvcMilliseconds));
        Assert.All(requests, item => Assert.Equal(20, item.BeforeMvcMilliseconds));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("3600001")]
    public void InvalidHostingDurationDoesNotInventRequestPhases(string elapsed)
    {
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) }, lines.Add);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1.0);
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", elapsed);
        Assert.Single(lines);
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(lines[0]));
    }

    [Fact]
    public void IncoherentDurationsAndBackwardClockAreRejected()
    {
        var clock = new NumericalClock { Timestamp = 100 };
        var accessor = new HttpContextAccessor { HttpContext = Context(nameof(TasksController.GetTask)) };
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add, clock);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 100.0);
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 99.0);
        accessor.HttpContext = Context(nameof(TasksController.GetTask));
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 100.0);
        clock.Timestamp = 99;
        Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 200.0);
        Assert.Equal(2, lines.Count);
        Assert.Empty(lines.Select(OwnedTaskReadDiagnostics.ReadRequestMeasurement).OfType<OwnedTaskReadDiagnostics.RequestMeasurement>());
    }

    [Fact]
    public void RequestTimingBudgetIsSeparateAndBounded()
    {
        var clock = new NumericalClock { Timestamp = 100 };
        var accessor = new HttpContextAccessor();
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(accessor, lines.Add, clock);
        for (var i = 0; i < 100; i++)
        {
            accessor.HttpContext = Context(nameof(TasksController.GetTask));
            Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 1.0);
            Emit(provider, OwnedTaskReadDiagnostics.RequestCategory, 2, "ElapsedMilliseconds", 2.0);
        }
        Assert.Equal(64, lines.Select(OwnedTaskReadDiagnostics.ReadMeasurement).OfType<OwnedTaskReadDiagnostics.Measurement>().Count());
        Assert.Equal(32, lines.Select(OwnedTaskReadDiagnostics.ReadRequestMeasurement).OfType<OwnedTaskReadDiagnostics.RequestMeasurement>().Count());
    }

    [Fact]
    public void ParentReaderRequiresCompleteCoherentNumericalRequestRecord()
    {
        var prefix = OwnedTaskReadDiagnostics.RequestPrefix;
        var record = new OwnedTaskReadDiagnostics.RequestMeasurement(nameof(TasksController.GetTask), 200, 100, 50, 50, DateTime.UtcNow.ToString("o"));
        Assert.NotNull(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + JsonSerializer.Serialize(record)));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + "{"));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + new string('x', 513)));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + JsonSerializer.Serialize(new { record.Action, record.AtUtc })));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + JsonSerializer.Serialize(record with { BeforeMvcMilliseconds = 100 })));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + JsonSerializer.Serialize(record with { Action = "private-action" })));
        Assert.Null(OwnedTaskReadDiagnostics.ReadRequestMeasurement(prefix + JsonSerializer.Serialize(record with { AfterMvcMilliseconds = -1 })));
    }

    [Theory]
    [InlineData(nameof(TasksController.GetTask))]
    [InlineData(nameof(TasksController.GetSettings))]
    [InlineData(nameof(TasksController.GetFileUploads))]
    [InlineData(nameof(TasksController.UploadFile))]
    public void HandlerTimingSharesTheExistingBoundedRecordWithoutReadingPrivateState(string action)
    {
        var request = Context(action);
        request.Items["private"] = new PrivateValue();
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(new HttpContextAccessor { HttpContext = request }, lines.Add);
        provider.ObserveHandler(request, 17.25);
        Assert.Empty(lines);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 25.0);
        var record = Assert.IsType<OwnedTaskReadDiagnostics.Measurement>(OwnedTaskReadDiagnostics.ReadMeasurement(Assert.Single(lines)));
        Assert.Equal(17.25, record.HandlerMilliseconds);
        Assert.DoesNotContain("private", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3600001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidHandlerTimingCannotEnterTheRecord(double milliseconds)
    {
        var request = Context(nameof(TasksController.GetTask));
        var lines = new List<string>();
        using var provider = new OwnedTaskReadDiagnostics(new HttpContextAccessor { HttpContext = request }, lines.Add);
        provider.ObserveHandler(request, milliseconds);
        Emit(provider, OwnedTaskReadDiagnostics.ActionCategory, 105, "ElapsedMilliseconds", 25.0);
        var record = OwnedTaskReadDiagnostics.ReadMeasurement(Assert.Single(lines))!;
        Assert.Null(record.HandlerMilliseconds);
        if (double.IsFinite(milliseconds))
            Assert.Null(OwnedTaskReadDiagnostics.ReadMeasurement(OwnedTaskReadDiagnostics.Prefix
                + JsonSerializer.Serialize(record with { HandlerMilliseconds = milliseconds })));
    }

    private sealed class NumericalClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private static void Emit(OwnedTaskReadDiagnostics provider, string category, int id, string key, object elapsed)
        => provider.CreateLogger(category).Log(LogLevel.Information, new EventId(id), new KeyValuePair<string, object?>[]
        {
            new(key, elapsed), new("commandText", new PrivateValue()), new("ActionName", "private-action-and-identity")
        }, null, (_, _) => throw new InvalidOperationException("Private formatter must never run."));

    private sealed class PrivateValue
    {
        public override string ToString() => throw new InvalidOperationException("Private SQL must never be rendered.");
    }

    private static DefaultHttpContext Context(string action)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(TasksController).GetTypeInfo(),
            MethodInfo = typeof(TasksController).GetMethod(action)!
        }), "private-display-name"));
        return context;
    }
}
