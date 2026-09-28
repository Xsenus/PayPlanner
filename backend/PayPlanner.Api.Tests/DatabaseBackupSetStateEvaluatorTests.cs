using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupSetStateEvaluatorTests
{
    private static DatabaseBackupCopy Copy(string provider, string status, string connection = "") => new()
    { ProviderKey = provider, ConnectionId = connection, Status = status };
    private static DatabaseBackupCopy Local => Copy("local", DatabaseBackupCopyStatuses.Verified);

    [Fact]
    public void LocalOnlyIgnoresHistoricalCloudFailures()
    {
        Assert.Equal(DatabaseBackupSetStatuses.LocalVerified, DatabaseBackupSetStateEvaluator.Evaluate(
            [Local, Copy("s3", "failed", "old")], false, [], false));
    }

    [Theory]
    [InlineData("verified", "verifiedConfigured")]
    [InlineData("pending", "replicating")]
    [InlineData("failed", "degraded")]
    [InlineData("deleted", "degraded")]
    [InlineData("unknown", "degraded")]
    public void EverySelectedDestinationMustBeConfirmed(string secondStatus, string expected)
    {
        Assert.Equal(expected, DatabaseBackupSetStateEvaluator.Evaluate(
            [Local, Copy("s3", "verified", "first"), Copy("s3", secondStatus, "second"), Copy("yandexDisk", "verified")],
            true, ["first", "second", "second"], true));
    }

    [Fact]
    public void NewDestinationDoesNotInheritOldSuccess()
    {
        var copies = new[] { Local, Copy("s3", "verified", "old") };
        Assert.Equal(DatabaseBackupSetStatuses.VerifiedConfigured, DatabaseBackupSetStateEvaluator.Evaluate(copies, true, ["old"], false));
        Assert.Equal(DatabaseBackupSetStatuses.AwaitingCopies, DatabaseBackupSetStateEvaluator.Evaluate(copies, true, ["new"], false));
        Assert.Equal(DatabaseBackupSetStatuses.AwaitingCopies, DatabaseBackupSetStateEvaluator.Evaluate(copies, true, ["old"], true));
        Assert.Equal("verified", copies[1].Status);
    }

    [Fact]
    public void EnabledS3WithoutConnectionsCannotBeConfirmed()
    {
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, DatabaseBackupSetStateEvaluator.Evaluate([Local], true, [], false));
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, DatabaseBackupSetStateEvaluator.Evaluate([Local], true, [" "], true));
    }

    [Fact]
    public void MissingOrDeletedLocalCopyIsNotPresentedAsACompleteSet()
    {
        Assert.Equal(DatabaseBackupSetStatuses.NoLocalCopy, DatabaseBackupSetStateEvaluator.Evaluate([], false, [], false));
        Assert.Equal(DatabaseBackupSetStatuses.NoLocalCopy, DatabaseBackupSetStateEvaluator.Evaluate(
            [Copy("local", "deleted"), Copy("s3", "verified", "first")], true, ["first"], false));
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, DatabaseBackupSetStateEvaluator.Evaluate([Copy("local", "failed")], false, [], false));
    }

    [Fact]
    public void FailureTakesPriorityOverAnotherPendingUpload()
    {
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, DatabaseBackupSetStateEvaluator.Evaluate(
            [Local, Copy("s3", "pending", "first"), Copy("s3", "failed", "second")], true, ["first", "second"], false));
    }
}
