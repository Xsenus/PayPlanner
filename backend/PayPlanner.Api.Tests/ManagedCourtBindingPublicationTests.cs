using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, "put")]
    [InlineData(true, "put")]
    public async Task CourtBindManaged_ChangedInstanceOrActorCannotPublishAfterIo(bool postgres, string phase)
    {
        await using var f = await CreateFixtureAsync(postgres, 1, autoPublication: true);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        var changed = false;
        async Task ChangeAsync()
        {
            if (changed) return;
            changed = true;
            Assert.Null(f.Db.Database.CurrentTransaction);
            if (phase == "access") await f.Db.Users.Where(row => row.Id == f.User.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
            else await f.Db.ClientCaseCourtInstances.Where(row => row.ClientCaseId == f.Case.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://owned--test.sudrf.ru/manual"));
        }
        if (phase == "put") f.Local.BeforePutAsync = _ => ChangeAsync();
        else f.Local.BeforeReadAsync = _ => ChangeAsync();
        var response = await f.Controller(source).BindCourtSyncCase(f.Case.Id, new CasesV2Controller.ClientCaseCourtSyncBindRequest
        { Instance = "appeal", SourceUrl = f.Case.CourtUrl, ConfirmDifferences = true }, default);
        Assert.True(changed);
        if (phase == "access") Assert.IsType<UnauthorizedResult>(response.Result);
        else Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.TaskJournalItems.AsNoTracking().ToArrayAsync());
        Assert.Null((await f.Db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtBindManaged_ResumedReadChecksInstanceAndActor(bool postgres, bool withdrawAccess)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, autoPublication: true);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        Assert.Equal(1, (await GenerateCourtAsync(f, source)).PendingCourtActDocuments);
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        var previousSuccess = (await f.Db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt;
        Assert.NotNull(previousSuccess);
        var changed = false;
        f.Local.BeforeReadAsync = async _ =>
        {
            if (changed) return;
            changed = true;
            Assert.Null(f.Db.Database.CurrentTransaction);
            await using var observer = f.Observer();
            if (withdrawAccess) await observer.Users.Where(row => row.Id == f.User.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
            else await observer.ClientCaseCourtInstances.Where(row => row.ClientCaseId == f.Case.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://owned--test.sudrf.ru/manual"));
        };
        var response = await f.Controller(source).BindCourtSyncCase(f.Case.Id, new CasesV2Controller.ClientCaseCourtSyncBindRequest
        { Instance = "appeal", SourceUrl = f.Case.CourtUrl, ConfirmDifferences = true }, default);
        Assert.True(changed);
        if (withdrawAccess) Assert.IsType<UnauthorizedResult>(response.Result);
        else Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.TaskJournalItems.AsNoTracking().ToArrayAsync());
        Assert.Equal(previousSuccess, (await f.Db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

}
