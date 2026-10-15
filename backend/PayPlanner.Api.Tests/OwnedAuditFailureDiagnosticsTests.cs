using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnedAuditFailureDiagnosticsTests
{
    [Theory]
    [InlineData(5, 517)]
    [InlineData(6, 262)]
    [InlineData(1, 1)]
    public void SqliteFailureKeepsOnlyNumericCodesFromItsException(int code, int extended)
    {
        var failure = new InvalidOperationException("synthetic-private", new SqliteException("synthetic-private", code, extended));
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Format(failure, null));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"SqliteErrorCode\":" + code + ",", summary);
        Assert.Contains("\"SqliteExtendedErrorCode\":" + extended + ",", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Theory]
    [InlineData("\"synthetic-private\"")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("1.5")]
    public void MalformedSqliteCodesCannotHideFailureOrExposeText(string value)
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Prefix + "{\"ExceptionTypes\":[],\"ApplicationFrames\":[],\"RequestAction\":\"unknown\",\"Canceled\":false,\"SqliteErrorCode\":" + value + ",\"SqliteExtendedErrorCode\":" + value + "}");
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"SqliteErrorCode\":null", summary);
        Assert.Contains("\"SqliteExtendedErrorCode\":null", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Fact]
    public void RuntimePressureFactsAreNumericAndSurviveTheSafeRecorder()
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Format(new InvalidOperationException("private-value"), null));
        var summary = recorder.FailureSummary();
        Assert.Matches("\"ThreadPoolThreads\":[0-9]+", summary);
        Assert.Matches("\"ThreadPoolPending\":[0-9]+", summary);
        Assert.Matches("\"GcPauseTotalMs\":[0-9]+", summary);
        Assert.DoesNotContain("private-value", summary);
    }

    [Fact]
    public void SlowCommandObservationsRetainOnlyBoundedTimingAndDoNotCreateFailures()
    {
        var recorder = new RestoredServerErrorRecorder();
        for (var index = 0; index < 20; index++)
            recorder.Receive($"  Slow database command INSERT (SaveChanges, Microsoft.EntityFrameworkCore.Sqlite, PaymentContext) completed in {1000 + index}.50 ms [Fingerprint=0123456789ABCDEF].");
        Assert.Empty(recorder.FailureSummary());
        recorder.Receive("fail: Example.Service[0]");
        var summary = recorder.FailureSummary();
        Assert.Equal(8, summary.Split("Owned slow command:").Length - 1);
        Assert.Contains("1019.50 ms", summary);
        Assert.DoesNotContain("1000.50", summary);
        Assert.DoesNotContain("0123456789ABCDEF", summary);
    }

    [Theory]
    [InlineData("SELECT private-value")]
    [InlineData("INSERT (SaveChanges, Microsoft.EntityFrameworkCore.Sqlite, PaymentContext) completed in 5000.00 ms [Fingerprint=0123456789ABCDEF]. private-value")]
    public void SlowCommandDiagnosticsRejectUntrustedDetails(string suffix)
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("  Slow database command " + suffix);
        recorder.Receive("fail: Example.Service[0]");
        Assert.DoesNotContain("Owned slow command", recorder.FailureSummary());
        Assert.DoesNotContain("private-value", recorder.FailureSummary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorDistinguishesRequestTokenFromIndependentAuditToken(bool sameToken)
    {
        using var requestToken = new CancellationTokenSource();
        using var auditToken = new CancellationTokenSource();
        var context = Context(requestToken.Token);
        requestToken.Cancel();
        auditToken.Cancel();
        var lines = new List<string>();
        using var provider = new OwnedAuditFailureDiagnostics(new HttpContextAccessor { HttpContext = context }, lines.Add);
        var logger = provider.CreateLogger(typeof(UserActivityService).FullName!);
        logger.LogError(new OperationCanceledException(sameToken ? requestToken.Token : auditToken.Token), "private logging state");
        var line = Assert.Single(lines);
        using var json = JsonDocument.Parse(line[OwnedAuditFailureDiagnostics.Prefix.Length..]);
        Assert.Equal(sameToken ? "request" : "other", json.RootElement.GetProperty("CancellationSource").GetString());
        Assert.True(json.RootElement.GetProperty("CancellationTokenRequested").GetBoolean());
        Assert.True(json.RootElement.GetProperty("RequestAbortedAtFailure").GetBoolean());
        Assert.EndsWith("UserActivityController.CreateAsync", json.RootElement.GetProperty("RequestAction").GetString());
        Assert.False(json.RootElement.TryGetProperty("DurationMilliseconds", out _));
    }

    [Fact]
    public void DiagnosticNeverSerializesExceptionMessageDataOrHttpSecrets()
    {
        var context = Context(CancellationToken.None);
        context.Request.QueryString = new QueryString("?access_token=synthetic-private-token");
        context.Request.Headers.Authorization = "Bearer synthetic-private-password";
        var failure = new InvalidOperationException("synthetic-private-sql");
        failure.Data["password"] = "synthetic-private-data";
        var line = OwnedAuditFailureDiagnostics.Format(failure, context);
        Assert.DoesNotContain("synthetic-private", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("UserActivityController.CreateAsync", line);
        Assert.Contains("\"Canceled\":false", line);
        Assert.Contains("\"CancellationSource\":\"unavailable\"", line);
        Assert.True(line.Length < 4096);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("write-admission")]
    [InlineData("commit")]
    public void MeasuredWriteStagesRemainBoundedAndDoNotExposeLoggingState(string stage)
    {
        var lines = new List<string>();
        using var provider = new OwnedAuditFailureDiagnostics(new HttpContextAccessor(), lines.Add);
        var logger = provider.CreateLogger(typeof(UserActivityService).FullName!);
        logger.LogError(new OperationCanceledException("synthetic-private"),
            "stage={AuditStage}, total={AuditTotalMs}, lock={AuditLockWaitMs}, context={AuditContextMs}, connection={AuditConnectionMs}, admission={AuditAdmissionMs}, save={AuditSaveMs}, commit={AuditCommitMs}, secret={Secret}",
            stage, 5001L, 0L, 0, 4999d, 1d, 0d, 1d, "synthetic-private");
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: PayPlanner.Api.Services.UserActivity.UserActivityService[0]");
        recorder.Receive(Assert.Single(lines));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"AuditStage\":\"" + stage + "\"", summary);
        Assert.Contains("\"AuditTotalMs\":5001", summary);
        Assert.Contains("\"AuditConnectionMs\":4999", summary);
        Assert.Contains("\"AuditAdmissionMs\":1", summary);
        Assert.Contains("\"AuditCommitMs\":1", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Fact]
    public void InvalidStageAndTimingValuesCannotExposeStateOrSuppressFailure()
    {
        var state = new Dictionary<string, object?>
        {
            ["AuditStage"] = "synthetic-private",
            ["AuditTotalMs"] = double.NaN,
            ["AuditLockWaitMs"] = -1,
            ["AuditContextMs"] = 3_600_001L,
            ["AuditConnectionMs"] = "synthetic-private",
            ["AuditSaveMs"] = "synthetic-private",
            ["AuditAdmissionMs"] = "synthetic-private",
            ["AuditCommitMs"] = double.NaN
        };
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Format(new InvalidOperationException(), null, state));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"AuditStage\":\"unavailable\"", summary);
        Assert.Contains("\"AuditTotalMs\":null", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("write-admission")]
    public void QueueOwnerFactsSurviveBothPrivacyFiltersWithoutIdentity(string ownerStage)
    {
        var state = new Dictionary<string, object?>
        {
            ["AuditQueueOwnerStage"] = ownerStage,
            ["AuditQueueOwnerMs"] = 4900L,
            ["AuditQueueOwnerStageMs"] = 300L,
            ["AuditQueueOwnerConnectionMs"] = 100L,
            ["AuditQueueOwnerAdmissionMs"] = 200L,
            ["AuditQueueOwnerLockMs"] = 0L,
            ["AuditQueueOwnerSaveMs"] = 3000L,
            ["AuditQueueOwnerCommitMs"] = 0L,
            ["AuditQueueCompletedAttempts"] = 3L,
            ["AuditQueueDepth"] = 8,
            ["OwnerIdentity"] = "synthetic-private"
        };
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Format(new OperationCanceledException("synthetic-private"), null, state));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"AuditQueueOwnerStage\":\"" + ownerStage + "\"", summary);
        Assert.Contains("\"AuditQueueOwnerMs\":4900", summary);
        Assert.Contains("\"AuditQueueOwnerStageMs\":300", summary);
        Assert.Contains("\"AuditQueueOwnerConnectionMs\":100", summary);
        Assert.Contains("\"AuditQueueOwnerAdmissionMs\":200", summary);
        Assert.Contains("\"AuditQueueOwnerLockMs\":0", summary);
        Assert.Contains("\"AuditQueueOwnerSaveMs\":3000", summary);
        Assert.Contains("\"AuditQueueOwnerCommitMs\":0", summary);
        Assert.Contains("\"AuditQueueCompletedAttempts\":3", summary);
        Assert.Contains("\"AuditQueueDepth\":8", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Fact]
    public void InvalidQueueOwnerFactsCannotExposeStateOrSuppressFailure()
    {
        var state = new Dictionary<string, object?>
        {
            ["AuditQueueOwnerStage"] = "synthetic-private",
            ["AuditQueueOwnerMs"] = double.PositiveInfinity,
            ["AuditQueueOwnerStageMs"] = "synthetic-private",
            ["AuditQueueOwnerConnectionMs"] = -1L,
            ["AuditQueueOwnerAdmissionMs"] = double.NaN,
            ["AuditQueueOwnerLockMs"] = 3_600_001L,
            ["AuditQueueOwnerSaveMs"] = "synthetic-private",
            ["AuditQueueOwnerCommitMs"] = double.PositiveInfinity,
            ["AuditQueueCompletedAttempts"] = -1L,
            ["AuditQueueDepth"] = "synthetic-private"
        };
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Format(new OperationCanceledException(), null, state));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("\"AuditQueueOwnerStage\":\"unavailable\"", summary);
        Assert.Contains("\"AuditQueueOwnerMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerStageMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerConnectionMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerAdmissionMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerLockMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerSaveMs\":null", summary);
        Assert.Contains("\"AuditQueueOwnerCommitMs\":null", summary);
        Assert.Contains("\"AuditQueueCompletedAttempts\":null", summary);
        Assert.Contains("\"AuditQueueDepth\":null", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Fact]
    public void CancellationKeepsBoundedProviderSymbolsWithoutExceptionOrRequestDetails()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = Assert.Throws<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
        failure.Data["private"] = "synthetic-private-data";
        var line = OwnedAuditFailureDiagnostics.Format(failure, Context(CancellationToken.None));
        using var json = JsonDocument.Parse(line[OwnedAuditFailureDiagnostics.Prefix.Length..]);
        var frames = json.RootElement.GetProperty("ProviderFrames").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.InRange(frames.Length, 1, 4);
        Assert.All(frames, name => Assert.True(OwnedAuditFailureDiagnostics.IsProviderSymbol(name)));
        Assert.Contains(frames, name => name.StartsWith("System.Threading.", StringComparison.Ordinal));
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(line);
        Assert.Contains("System.Threading.", recorder.FailureSummary());
        Assert.DoesNotContain("synthetic-private", recorder.FailureSummary());
        Assert.Contains("Server fail log entries: 1.", recorder.FailureSummary());
    }

    [Fact]
    public void RecorderRejectsUntrustedProviderDetailsAndBoundsTheirCount()
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Prefix + JsonSerializer.Serialize(new
        {
            ExceptionTypes = Array.Empty<string>(),
            ApplicationFrames = Array.Empty<string>(),
            RequestAction = "unknown",
            Canceled = false,
            ProviderFrames = new[] { "Microsoft.EntityFrameworkCore.Storage.RelationalConnection.OpenAsync",
                "private_payload", "Microsoft.Data.Sqlite.Connection.Open?token=synthetic-private", "System.Threading.CancellationToken.ThrowIfCancellationRequested",
                "Microsoft.Data.Sqlite.Excess.Missing" }
        }));
        var summary = recorder.FailureSummary();
        Assert.Contains("RelationalConnection.OpenAsync", summary);
        Assert.Contains("CancellationToken.ThrowIfCancellationRequested", summary);
        Assert.DoesNotContain("private", summary);
        Assert.DoesNotContain("Excess", summary);
        Assert.Contains("Server fail log entries: 1.", summary);
    }

    [Fact]
    public void ProviderDoesNotObserveUnrelatedCategoryOrNonErrorState()
    {
        var lines = new List<string>();
        using var provider = new OwnedAuditFailureDiagnostics(new HttpContextAccessor(), lines.Add);
        var exception = new InvalidOperationException("synthetic-private");
        provider.CreateLogger("Unrelated.Service").LogError(exception, "unrelated");
        provider.CreateLogger(typeof(UserActivityService).FullName!).LogWarning(exception, "warning");
        provider.CreateLogger(typeof(UserActivityService).FullName!).LogError("failure without exception");
        Assert.Empty(lines);
    }

    [Fact]
    public void RecorderKeepsRealErrorAndDropsArbitraryAuditPayloadFields()
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: PayPlanner.Api.Services.UserActivity.UserActivityService[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Prefix + """
            {"ExceptionTypes":["System.OperationCanceledException"],"ApplicationFrames":[],
             "RequestAction":"PayPlanner.Api.Controllers.UserActivityController.CreateAsync","Canceled":true,
             "CancellationSource":"request","CancellationTokenRequested":true,"RequestAbortedAtFailure":true,
             "Sql":"synthetic-private-sql","Password":"synthetic-private-password"}
            """);
        for (var i = 0; i < 600; i++) recorder.Receive("info: normal synthetic traffic");
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.Contains("fail: PayPlanner.Api.Services.UserActivity.UserActivityService[0]", summary);
        Assert.Contains("UserActivityController.CreateAsync", summary);
        Assert.Contains("\"CancellationSource\":\"request\"", summary);
        Assert.Contains("\"CancellationTokenRequested\":true", summary);
        Assert.DoesNotContain("synthetic-private", summary);
    }

    [Theory]
    [InlineData("not-json synthetic-private")]
    [InlineData("{\"Password\":\"synthetic-private\"}")]
    [InlineData("{\"ExceptionTypes\":[],\"ApplicationFrames\":[],\"RequestAction\":\"unknown\",\"Canceled\":\"synthetic-private\"}")]
    public void InvalidAuditDiagnosticCannotHideErrorOrExposeBody(string payload)
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Prefix + payload);
        Assert.Contains("fail: Example.Service[0]", recorder.FailureSummary());
        Assert.DoesNotContain("synthetic-private", recorder.FailureSummary());
    }

    [Fact]
    public void AuditDiagnosticsRemainBoundedAndCannotInventFailure()
    {
        var recorder = new RestoredServerErrorRecorder();
        for (var i = 0; i < 100; i++)
            recorder.Receive(OwnedAuditFailureDiagnostics.Format(new InvalidOperationException("private"), null));
        Assert.Equal(string.Empty, recorder.FailureSummary());
        recorder.Receive("fail: Example.Service[0]");
        recorder.Receive(OwnedAuditFailureDiagnostics.Prefix + new string('x', 5000));
        var summary = recorder.FailureSummary();
        Assert.Contains("Server fail log entries: 1.", summary);
        Assert.True(summary.Length <= 8000);
        Assert.DoesNotContain(new string('x', 100), summary);
    }

    private static DefaultHttpContext Context(CancellationToken requestToken)
    {
        var context = new DefaultHttpContext { RequestAborted = requestToken };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(UserActivityController).GetTypeInfo(),
                MethodInfo = typeof(UserActivityController).GetMethod(nameof(UserActivityController.CreateAsync))!
            }), "owned audit action"));
        return context;
    }
}
