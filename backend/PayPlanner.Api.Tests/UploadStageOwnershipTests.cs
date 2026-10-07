using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

// Pending baseline/qualification on real services after the frozen gates stop.
public sealed partial class ManagedProfilePhotoUploadServiceTests
{
    [Theory]
    [InlineData("primary")]
    [InlineData("compatibility")]
    public async Task PhotoCreateConflictPreservesUnownedStageAndItsCapacityDebt(string phase)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.UploadCapacityChecksEnabled = true;
        fixture.Options.UploadMinimumFreeBytes = 0;
        ForeignUploadStageAdmission? admission = null;
        var uploader = fixture.WithStage(stage => admission = new(stage, phase == "primary" ? 0 : 1));
        await Assert.ThrowsAsync<IOException>(() => uploader.UploadAsync(fixture.UserId, Guid.NewGuid(),
            new UntrustedFile([1, 2, 3, 4], 4), "https://crm.example.test", default));

        var foreign = Assert.IsType<StorageStageCapacityTicket>(admission!.ForeignTicket);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(foreign.AbsolutePath));
        fixture.Db.ChangeTracker.Clear();
        var pending = Assert.Single(await fixture.Db.StorageCapacityReservations.AsNoTracking()
            .Where(row => row.Status == StorageCapacityReservationStatuses.Pending).ToArrayAsync());
        Assert.Equal(foreign.ObjectId, pending.ObjectId);
        Assert.Equal(foreign.Key, pending.IdempotencyKey);
        Assert.Equal(fixture.OldUrl, (await fixture.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.False(await fixture.Db.StorageProductBindings.AnyAsync());
        Assert.Equal(phase == "primary" ? 0 : 1, await fixture.Db.StorageObjects.CountAsync());
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(
            Path.Combine(fixture.Root, "photos", fixture.UserId.ToString(), "old.png")));
    }
}

public sealed partial class ManagedTaskFileUploadServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TaskCreateConflictPreservesPrimaryTaskOrCaseStageAndItsCapacityDebt(int compatibilityCall)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.UploadCapacityChecksEnabled = true;
        fixture.Options.UploadMinimumFreeBytes = 0;
        ForeignUploadStageAdmission? admission = null;
        var uploader = fixture.NewService(fixture.Db,
            decorateStage: stage => admission = new(stage, compatibilityCall));
        await Assert.ThrowsAsync<IOException>(() => uploader.UploadAsync(fixture.Request(Guid.NewGuid()), default));

        var foreign = Assert.IsType<StorageStageCapacityTicket>(admission!.ForeignTicket);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(foreign.AbsolutePath));
        fixture.Db.ChangeTracker.Clear();
        var pending = Assert.Single(await fixture.Db.StorageCapacityReservations.AsNoTracking()
            .Where(row => row.Status == StorageCapacityReservationStatuses.Pending).ToArrayAsync());
        Assert.Equal(foreign.ObjectId, pending.ObjectId);
        Assert.Equal(foreign.Key, pending.IdempotencyKey);
        Assert.False(await fixture.Db.TaskJournalFiles.AnyAsync());
        Assert.False(await fixture.Db.ClientCaseDocuments.AnyAsync());
        Assert.False(await fixture.Db.StorageProductBindings.AnyAsync());
        Assert.Equal(compatibilityCall == 0 ? 0 : 2, await fixture.Db.StorageObjects.CountAsync());
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }
}

internal sealed class ForeignUploadStageAdmission(IStorageStageCapacityService inner, int compatibilityCall)
    : IStorageStageCapacityService
{
    private int compatibilityCalls;
    public StorageStageCapacityTicket? ForeignTicket { get; private set; }
    public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(
        string identity, string path, long size, CancellationToken ct)
    {
        var result = await inner.ReserveAsync(identity, path, size, ct);
        if (compatibilityCall == 0 && result.Ticket is { } ticket)
            await CreateForeignAsync(ticket, ct);
        return result;
    }
    public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(
        string identity, string path, long size, string alias, CancellationToken ct)
    {
        var result = await inner.ReserveCompatibilityAsync(identity, path, size, alias, ct);
        if (++compatibilityCalls == compatibilityCall && result.Ticket is { } ticket)
            await CreateForeignAsync(ticket, ct);
        return result;
    }
    private async Task CreateForeignAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        Assert.Null(ForeignTicket);
        await File.WriteAllBytesAsync(ticket.AbsolutePath, new byte[] { 9, 8, 7 }, ct);
        ForeignTicket = ticket;
    }
    public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket,
        long size, CancellationToken ct) => inner.ConfirmPhysicalSizeAsync(ticket, size, ct);
    public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
        => inner.SettlePhysicalAsync(ticket, ct);
    public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
        => inner.ReleaseAsync(ticket, ct);
    public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit,
        TimeSpan age, CancellationToken ct, string? afterKey = null)
        => inner.ReconcilePendingAsync(apply, limit, age, ct, afterKey);
}
