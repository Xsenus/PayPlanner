using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateMetadata_ExplicitSubmissionClearKeepsEachVersionsExistingRule(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db);
        var input = new ClientCase
        {
            ClientId = await db.Clients.Select(row => row.Id).SingleAsync(),
            Title = "Явная очистка при создании",
            StartDate = CreationSpoofTime,
            ExecutiveWritReceivedAt = CreationSpoofTime.AddDays(-1),
            ExecutiveWritSubmittedAt = CreationSpoofTime,
            ExecutiveWritSubmittedTo = "bank",
            ClearExecutiveWritSubmission = true,
        };
        var created = Assert.IsType<CreatedResult>(await CreateAtomicCaseAsync(db, actor, input, route, new BulkPublicationBoundary(db))); var reply = Assert.IsType<ClientCase>(created.Value); var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == reply.Id);
        Assert.Equal(route == 1 ? CreationSpoofTime : (DateTime?)null, saved.ExecutiveWritSubmittedAt); Assert.Equal(route == 1 ? "bank" : null, saved.ExecutiveWritSubmittedTo); Assert.Equal(CreationSpoofTime.AddDays(-1), saved.ExecutiveWritReceivedAt); Assert.Equal(CreationSpoofTime, saved.StartDate); Assert.Equal(saved.ExecutiveWritSubmittedAt, reply.ExecutiveWritSubmittedAt);
        Assert.Equal(3, await db.TaskJournalItems.CountAsync(row => ids.Contains(row.ClientCaseId!.Value))); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Status == "done")).Result); Assert.All(await db.ClientCases.AsNoTracking().Where(row => ids.Contains(row.Id)).ToArrayAsync(), row => { Assert.Equal("Исходная история", row.EventHistory); Assert.Equal(123.45m, row.CourtClaimAmount); }); Assert.Empty(await db.Payments.ToArrayAsync());
    }
}
