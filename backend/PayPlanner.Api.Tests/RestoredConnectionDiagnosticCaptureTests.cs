using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredConnectionDiagnosticCaptureTests
{
    [Fact]
    public void TypedDiagnosticsPersistWithFailureWithoutArbitraryPayloadFields()
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Microsoft.EntityFrameworkCore.Database.Connection[20004]");
        recorder.Receive("PP_RESTORED_CONNECTION_DIAGNOSTIC " + """
            {"ExceptionTypes":["System.Threading.Tasks.TaskCanceledException"],"Canceled":true,
             "DurationMilliseconds":2,"RequestAction":"PayPlanner.Api.Controllers.UserNotificationsController.GetMine",
             "ApplicationFrames":[],"CancellationSource":"request","RequestAbortedAtFailure":true,
             "Sql":"synthetic-private-sql-secret","Password":"synthetic-private-password-secret"}
            """);
        for (var i = 0; i < 600; i++) recorder.Receive("info: Example.Component[0] normal line");
        var result = recorder.FailureSummary();
        Assert.Contains("TaskCanceledException", result);
        Assert.Contains("UserNotificationsController.GetMine", result);
        Assert.Contains("\"Canceled\":true", result);
        Assert.Contains("\"CancellationSource\":\"request\"", result);
        Assert.Contains("\"RequestAbortedAtFailure\":true", result);
        Assert.DoesNotContain("synthetic-private", result);
    }

    [Theory]
    [InlineData("not-json synthetic-private-password-secret")]
    [InlineData("{\"Sql\":\"synthetic-private-password-secret\"}")]
    [InlineData("{\"ExceptionTypes\":[],\"ApplicationFrames\":[],\"RequestAction\":\"unknown\",\"Canceled\":true,\"DurationMilliseconds\":\"synthetic-private-password-secret\"}")]
    public void MalformedDiagnosticsCannotHideServerErrorOrExposeTheirBody(string payload)
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("fail: Example.Component[17]");
        recorder.Receive("PP_RESTORED_CONNECTION_DIAGNOSTIC " + payload);
        var result = recorder.FailureSummary();
        Assert.Contains("fail: Example.Component[17]", result);
        Assert.DoesNotContain("synthetic-private-password-secret", result);
    }

    [Fact]
    public void DiagnosticMarkerAloneDoesNotTurnNormalLogIntoServerFailure()
    {
        var recorder = new RestoredServerErrorRecorder();
        recorder.Receive("PP_RESTORED_CONNECTION_DIAGNOSTIC malformed diagnostic");
        Assert.Equal(string.Empty, recorder.FailureSummary());
    }
}
