using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCapacityBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static StorageCapacityResource Resource(string key, string group = "shared", long? free = 100, long reserve = 10)
        => new(key, group, free, reserve, Now);
    private static StorageCapacityBudgetReport Budget(StorageCapacityResource[] resources,
        StorageCapacityDemand[]? pending = null, StorageCapacityDemand[]? proposed = null)
        => StorageCapacityBudget.Evaluate(resources, pending ?? [], proposed ?? [], Now, TimeSpan.FromMinutes(1));

    [Fact]
    public void FoldersSharingDiskDoNotDoubleAvailableSpaceAndIncludeSpoolDebt()
    {
        var report = Budget([Resource("first"), Resource("second"), Resource("spool")],
            [new("first", 20, "pending")], [new("first", 30, "copy-a"), new("second", 30, "copy-b"), new("spool", 15, "spool-a")]);
        Assert.False(report.CanSchedule); var group = Assert.Single(report.Groups);
        Assert.Equal(100, group.AvailableBytes); Assert.Equal(20, group.PendingBytes);
        Assert.Equal(75, group.AdditionalBytes); Assert.Equal(5, group.RemainingBytes);
    }

    [Fact]
    public void ExactDurableReservationIsCountedOnceAcrossRetryAndDuplicateRows()
    {
        var demand = new StorageCapacityDemand("local", 70, "stable-copy");
        var report = Budget([Resource("local")], [demand, demand], [demand, new("local", 20, "new-copy")]);
        Assert.True(report.CanSchedule); var group = Assert.Single(report.Groups);
        Assert.Equal(70, group.PendingBytes); Assert.Equal(20, group.AdditionalBytes); Assert.Equal(10, group.RemainingBytes);
    }

    [Fact]
    public void IndependentGroupsCannotBorrowCapacityFromEachOther()
    {
        var report = Budget([Resource("small", "one", 20), Resource("large", "two", 1000)],
            proposed: [new("small", 11, "copy")]);
        Assert.False(report.CanSchedule); Assert.Equal(2, report.Groups.Count);
        Assert.False(report.Groups.Single(g => g.GroupId == "one").CanSchedule);
    }

    [Fact]
    public void SharedDiskUsesSmallestFreshFreeObservationAndStrictestReserve()
    {
        var report = Budget([Resource("a", free: 100), Resource("b", free: 50, reserve: 20)], proposed: [new("a", 31, "copy")]);
        Assert.False(report.CanSchedule); var group = Assert.Single(report.Groups);
        Assert.Equal(50, group.AvailableBytes); Assert.Equal(20, group.MinimumFreeBytes); Assert.Equal(19, group.RemainingBytes);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("negative")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("duplicate")]
    public void InvalidOrUnknownObservationCannotCertifyCapacity(string scenario)
    {
        var resource = Resource("local");
        resource = scenario switch
        {
            "unknown" => resource with { AvailableBytes = null },
            "negative" => resource with { AvailableBytes = -1 },
            "stale" => resource with { ObservedAt = Now.AddMinutes(-2) },
            "future" => resource with { ObservedAt = Now.AddSeconds(1) },
            _ => resource
        };
        var report = Budget(scenario == "duplicate" ? [resource, resource] : [resource], proposed: [new("local", 1, "copy")]);
        Assert.False(report.CanSchedule);
    }

    [Theory]
    [InlineData("unknownResource")]
    [InlineData("negativeDemand")]
    [InlineData("conflictingIdentity")]
    [InlineData("overflow")]
    public void InvalidAuthorityAndOverflowFailClosed(string scenario)
    {
        var pending = new StorageCapacityDemand("local", 1, "existing");
        StorageCapacityDemand[] proposed = scenario switch
        {
            "unknownResource" => [new("other", 1, "copy")],
            "negativeDemand" => [new("local", -1, "copy")],
            "conflictingIdentity" => [pending with { Bytes = 2 }],
            _ => [new("local", long.MaxValue, "copy")]
        };
        var report = Budget([Resource("local", free: long.MaxValue)], [pending], proposed);
        Assert.False(report.CanSchedule); Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void UnknownSpoolVolumeCannotBeIgnoredEvenWhenDestinationsFit()
    {
        var report = Budget([Resource("local"), Resource("spool", "temp", null)],
            proposed: [new("local", 20, "copy"), new("spool", 20, "spool")]);
        Assert.False(report.CanSchedule); Assert.Null(report.Groups.Single(g => g.GroupId == "temp").RemainingBytes);
    }
}
