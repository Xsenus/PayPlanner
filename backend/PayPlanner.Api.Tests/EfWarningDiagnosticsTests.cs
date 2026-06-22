using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public class EfWarningDiagnosticsTests
{
    [Fact]
    public void PaymentContextModel_DoesNotWarnAboutRequiredFilteredNavigations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings =>
                warnings.Throw(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;

        using var context = new PaymentContext(options);
        Assert.NotEmpty(context.Model.GetEntityTypes());
    }

    [Fact]
    public async Task BackgroundCollectionQueries_UseSplitQueries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings =>
                warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning))
            .Options;

        await using var context = new PaymentContext(options);
        await context.Database.EnsureCreatedAsync();

        await context.GoogleCalendarConnections
            .AsNoTracking()
            .Include(item => item.User)
            .ThenInclude(item => item!.Role)
            .Include(item => item.TaskLinks)
            .Include(item => item.Calendars)
            .ThenInclude(item => item.OwnerUser)
            .AsSplitQuery()
            .ToListAsync();

        await context.YandexCalendarConnections
            .AsNoTracking()
            .Include(item => item.User)
            .ThenInclude(item => item!.Role)
            .Include(item => item.TaskLinks)
            .Include(item => item.Calendars)
            .ThenInclude(item => item.OwnerUser)
            .AsSplitQuery()
            .ToListAsync();

        await context.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.ClientCase)
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .AsSplitQuery()
            .ToListAsync();

        await context.StickyNotes
            .AsNoTracking()
            .Include(item => item.Board)
            .ThenInclude(board => board!.Members)
            .Include(item => item.AssignedUser)
            .ThenInclude(user => user!.Role)
            .ThenInclude(role => role!.Permissions)
            .AsSplitQuery()
            .ToListAsync();
    }
}
