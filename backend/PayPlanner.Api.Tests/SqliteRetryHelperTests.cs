using Microsoft.Data.Sqlite;
using PayPlanner.Api.Helpers;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SqliteRetryHelperTests
{
    [Theory]
    [InlineData(false, 5, 5, true)]
    [InlineData(false, 6, 262, false)]
    [InlineData(true, 6, 262, true)]
    [InlineData(true, 6, 518, false)]
    [InlineData(true, 1, 1, false)]
    [InlineData(true, 5, 517, true)]
    public async Task SharedCacheRetryRequiresOptInAndDoesNotBroadenOtherErrors(
        bool optIn, int code, int extended, bool retry)
    {
        var calls = 0;
        var expected = new SqliteException("synthetic failure", code, extended);
        Task<int> Attempt(CancellationToken _) => ++calls == 1
            ? Task.FromException<int>(expected) : Task.FromResult(42);

        var pending = SqliteRetryHelper.ExecuteAsync(Attempt, null, CancellationToken.None,
            initialDelayMs: 1, retrySharedCacheLock: optIn);
        if (retry)
        {
            Assert.Equal(42, await pending);
            Assert.Equal(2, calls);
        }
        else
        {
            Assert.Same(expected, await Assert.ThrowsAsync<SqliteException>(() => pending));
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task SharedCacheRetryRetainsItsAttemptLimitAndFinalFailure()
    {
        var calls = 0;
        var expected = new SqliteException("synthetic failure", 6, 262);
        Task<int> Attempt(CancellationToken _) { calls++; return Task.FromException<int>(expected); }
        var failure = await Assert.ThrowsAsync<SqliteException>(() => SqliteRetryHelper.ExecuteAsync(
            Attempt, null, CancellationToken.None, maxRetries: 2, initialDelayMs: 1, retrySharedCacheLock: true));
        Assert.Same(expected, failure);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SharedCacheRetryStopsAtCancellationBeforeAnotherAttempt()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<int> Attempt(CancellationToken _) { calls++; return Task.FromException<int>(new SqliteException("synthetic failure", 6, 262)); }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteRetryHelper.ExecuteAsync(
            Attempt, null, cancellation.Token, initialDelayMs: 1, retrySharedCacheLock: true));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, calls);
    }
}
