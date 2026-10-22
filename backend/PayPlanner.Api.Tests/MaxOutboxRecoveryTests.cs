using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredFinalAttemptReservesOnlyRecoveryDayWithoutResending(bool previousDay)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        var interrupted = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        interrupted.Status = MaxOutboxStatuses.Processing;
        interrupted.AttemptCount = interrupted.MaxAttempts;
        interrupted.UpdatedAt = fixture.Now.AddMinutes(-6).AddDays(previousDay ? -1 : 0);
        interrupted.NextAttemptAt = interrupted.UpdatedAt;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(MaxOutboxStatuses.Failed, interrupted.Status);
        Assert.Equal("delivery-outcome-unknown", interrupted.LastErrorCode);
        Assert.Equal(interrupted.MaxAttempts, interrupted.AttemptCount);
        Assert.Null(interrupted.SentAt);
        Assert.Null(interrupted.ProviderMessageId);
        Assert.Empty(api.Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.RetryAsync(
            interrupted.PublicId, fixture.UserId, "Администратор", CancellationToken.None));

        var nextDay = new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc);
        var pending = await fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "recovery:next" }, CancellationToken.None);
        Assert.Equal(nextDay, pending.NextAttemptAt);
        // Имитируем уже накопленную запись, которую нужно повторно проверить в worker.
        pending.NextAttemptAt = fixture.Now;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(nextDay, pending.NextAttemptAt);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Empty(api.Messages);

        fixture.MoveTimeTo(new DateTimeOffset(nextDay));
        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(MaxOutboxStatuses.Sent, pending.Status);
        Assert.Single(api.Messages);
        Assert.Equal(0, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(MaxOutboxStatuses.Failed, interrupted.Status);
        Assert.Equal("delivery-outcome-unknown", interrupted.LastErrorCode);
        Assert.Equal(2, await fixture.Db.MaxOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ExpiredFinalAttemptReservesQuotaBeforeOlderQueuedItemInSameBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var interrupted = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        var pending = await fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "recovery:older-backlog" }, CancellationToken.None);
        interrupted.Status = MaxOutboxStatuses.Processing;
        interrupted.AttemptCount = interrupted.MaxAttempts;
        interrupted.UpdatedAt = fixture.Now.AddMinutes(-6);
        interrupted.NextAttemptAt = interrupted.UpdatedAt;
        pending.NextAttemptAt = fixture.Now.AddDays(-1);
        await fixture.Db.SaveChangesAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        var api = new RecordingApiClient();

        Assert.Equal(2, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Failed, interrupted.Status);
        Assert.Equal("delivery-outcome-unknown", interrupted.LastErrorCode);
        Assert.Equal(MaxOutboxStatuses.Queued, pending.Status);
        Assert.Equal(new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc), pending.NextAttemptAt);
        Assert.Equal(0, pending.AttemptCount);
    }

    [Fact]
    public async Task FreshFinalAttemptStillReservesQuotaAcrossLocalMidnight()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConfigureLimitsAsync(quietHoursEnabled: false, dailyLimit: 1);
        var inFlight = await fixture.Outbox.QueueAsync(fixture.Request(), CancellationToken.None);
        inFlight.Status = MaxOutboxStatuses.Processing;
        inFlight.AttemptCount = inFlight.MaxAttempts;
        inFlight.UpdatedAt = new DateTime(2026, 7, 31, 16, 59, 0, DateTimeKind.Utc);
        await fixture.Db.SaveChangesAsync();
        fixture.MoveTimeTo(new DateTimeOffset(2026, 7, 31, 17, 1, 0, TimeSpan.Zero));

        var pending = await fixture.Outbox.QueueAsync(
            fixture.Request() with { EventKey = "midnight:final-in-flight" }, CancellationToken.None);
        var nextDay = new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc);
        Assert.Equal(nextDay, pending.NextAttemptAt);
        pending.NextAttemptAt = fixture.Now;
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        Assert.Equal(1, await fixture.Processor(api).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(MaxOutboxStatuses.Processing, inFlight.Status);
        Assert.Equal(inFlight.MaxAttempts, inFlight.AttemptCount);
        Assert.Equal(nextDay, pending.NextAttemptAt);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Empty(api.Messages);
    }
}
