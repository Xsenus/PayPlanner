using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnedAuditFailureDiagnosticsTests
{
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
