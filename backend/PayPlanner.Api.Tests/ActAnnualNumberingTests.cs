using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ActAnnualNumberingTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_OneYearSharesOneSeriesAcrossDaysAndStartsTheNextYearAtOne(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();

        Assert.Equal("1", await allocator.NextAsync(new DateTime(2001, 1, 1), default));
        Assert.Equal("2", await allocator.NextAsync(new DateTime(2001, 12, 31), default));
        Assert.Equal("1", await allocator.NextAsync(new DateTime(2002, 1, 1), default));
        Assert.Equal("3", await allocator.NextAsync(new DateTime(2001, 2, 3), default));
        await transaction.CommitAsync();

        Assert.Equal(3, (await scope.Db.ActAnnualNumberCounters.AsNoTracking().SingleAsync(item => item.Year == 2001)).LastNumber);
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.AsNoTracking().SingleAsync(item => item.Year == 2002)).LastNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_HistoricalFloorIncludesArchivedDuplicatesAndLongLeadingZeroes(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var date = new DateTime(2003, 9, 2);
        var archived = new Act { Number = "40", Date = date, ClientId = scope.Source.OwnClient.Id };
        scope.Db.Acts.AddRange(archived,
            new Act { Number = "0007", Date = date },
            new Act { Number = "7", Date = date },
            new Act { Number = "7", Date = date.AddDays(1) },
            new Act { Number = "000000000000000000000000000070", Date = date },
            new Act { Number = "А5000", Date = date },
            new Act { Number = "7-К1", Date = date },
            new Act { Number = "9000", Date = date.AddYears(1) });
        await scope.Db.SaveChangesAsync();
        scope.Db.Entry(archived).Property<bool>("IsDeleted").CurrentValue = true;
        await scope.Db.SaveChangesAsync();
        var before = await scope.Db.Acts.IgnoreQueryFilters().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync();
        var allocator = new ActAnnualNumberingService(scope.Db);

        Assert.Equal("71", await allocator.PreviewNextAsync(date, default));
        await using (var transaction = await scope.Db.Database.BeginTransactionAsync())
        {
            Assert.Equal("71", await allocator.NextAsync(date, default));
            await transaction.CommitAsync();
        }

        Assert.Equal(before, await scope.Db.Acts.IgnoreQueryFilters().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_PreviewDoesNotCreateOrAdvanceCounter(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        var date = new DateTime(2004, 12, 31);

        Assert.Equal("1", await allocator.PreviewNextAsync(date, default));
        Assert.Equal("1", await allocator.PreviewNextAsync(date, default));
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("1", await allocator.NextAsync(date, default));
        await transaction.CommitAsync();
        Assert.Equal("2", await allocator.PreviewNextAsync(date, default));
        Assert.Equal(1, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_RollbackBeforeAuthoritativeCommitRestoresTheNumber(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        var date = new DateTime(2005, 1, 1);
        await using (var transaction = await scope.Db.Database.BeginTransactionAsync())
        {
            Assert.Equal("1", await allocator.NextAsync(date, default));
            await transaction.RollbackAsync();
        }
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
        await using (var transaction = await scope.Db.Database.BeginTransactionAsync())
        {
            Assert.Equal("1", await allocator.NextAsync(date, default));
            await transaction.CommitAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_LateHistoricalFloorOnlyMovesForwardAndKeepsTrackerClean(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        var date = new DateTime(2006, 5, 3);
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("1", await allocator.NextAsync(date, default));
        await allocator.RaiseFloorAsync(date, "00037", default);
        await allocator.RaiseFloorAsync(date, "5", default);
        await allocator.RaiseFloorAsync(date, "37-К1", default);
        Assert.False(scope.Db.ChangeTracker.HasChanges());
        Assert.Equal("38", await allocator.NextAsync(date, default));
        await transaction.CommitAsync();
        Assert.Equal(38, (await scope.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
    }

    [Theory]
    [InlineData(false, "9223372036854775807")]
    [InlineData(true, "9223372036854775807")]
    public async Task AnnualAct_ExhaustedHistoricalYearDoesNotWrapOrStartAgain(bool postgres, string historical)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var date = new DateTime(2007, 5, 3);
        scope.Db.Acts.Add(new Act { Number = historical, Date = date });
        await scope.Db.SaveChangesAsync();
        var allocator = new ActAnnualNumberingService(scope.Db);
        await Assert.ThrowsAsync<ActAnnualNumberExhaustedException>(() => allocator.PreviewNextAsync(date, default));
        await using (var transaction = await scope.Db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<ActAnnualNumberExhaustedException>(() => allocator.NextAsync(date, default));
            await transaction.RollbackAsync();
        }
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "18446744073709551616")]
    [InlineData(true, "18446744073709551616")]
    [InlineData(false, "9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999")]
    [InlineData(true, "9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999")]
    public async Task AnnualAct_IdentifiersOutsideTheGeneratedRangeArePreservedAndDoNotPoisonTheSeries(bool postgres, string historical)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var date = new DateTime(2007, 6, 3);
        var act = new Act { Number = historical, Date = date };
        scope.Db.Acts.Add(act);
        await scope.Db.SaveChangesAsync();
        var allocator = new ActAnnualNumberingService(scope.Db);
        Assert.Equal("1", await allocator.PreviewNextAsync(date, default));
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("1", await allocator.NextAsync(date, default));
        await allocator.RaiseFloorAsync(date, historical, default);
        Assert.Equal("2", await allocator.NextAsync(date, default));
        await transaction.CommitAsync();
        Assert.Equal(historical, (await scope.Db.Acts.AsNoTracking().SingleAsync(item => item.Id == act.Id)).Number);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 9999)]
    [InlineData(true, 9999)]
    public async Task AnnualAct_SupportsBothDateOnlyYearBoundaries(bool postgres, int year)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("1", await allocator.NextAsync(new DateTime(year, 1, 1), default));
        Assert.Equal("2", await allocator.NextAsync(new DateTime(year, 12, 31), default));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_HistoryIncludesTheExactMaximumDateTime(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        scope.Db.Acts.Add(new Act { Number = "50", Date = DateTime.MaxValue });
        await scope.Db.SaveChangesAsync();
        var allocator = new ActAnnualNumberingService(scope.Db);
        Assert.Equal("51", await allocator.PreviewNextAsync(new DateTime(9999, 12, 31), default));
        await using var transaction = await scope.Db.Database.BeginTransactionAsync();
        Assert.Equal("51", await allocator.NextAsync(new DateTime(9999, 12, 31), default));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_AllocationRequiresCallerTransaction(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var allocator = new ActAnnualNumberingService(scope.Db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => allocator.NextAsync(new DateTime(2008, 1, 1), default));
        Assert.Empty(await scope.Db.ActAnnualNumberCounters.ToArrayAsync());
    }
}
