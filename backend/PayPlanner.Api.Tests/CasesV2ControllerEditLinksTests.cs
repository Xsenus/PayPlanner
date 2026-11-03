using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseEditLinks_HttpRejectsAChangedInactiveResponsible(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        var assignee = await CreateUserAsync(db, "edit-inactive@example.local", "Отключённый", DataVisibilityScope.All); await db.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var request = EditRequest(source); request.ResponsibleUserId = assignee.Id; request.Title = "Не сохранять";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseEditLinks_HttpKeepsUnchangedHiddenOwnerOfAnAssignedCase(bool legacy, bool legal)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var outsider = await CreateUserAsync(db, "edit-parent-outsider@example.local", "Другой", DataVisibilityScope.All);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        const string ownerName = "Владелец назначенного дела";
        const string privateNote = "Закрытые заметки, которые не должны передаваться с делом";
        if (legal)
        {
            var owner = new LegalEntity { ShortName = ownerName, Notes = privateNote, ResponsibleUserId = outsider.Id }; db.LegalEntities.Add(owner); await db.SaveChangesAsync();
            await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, (int?)null).SetProperty(row => row.LegalEntityId, owner.Id));
        }
        else await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, ownerName).SetProperty(row => row.Notes, privateNote).SetProperty(row => row.ResponsibleUserId, outsider.Id));
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var request = EditRequest(source); request.Title = "Правка назначенного дела";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var reply = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(privateNote, reply); Assert.Contains(ownerName, reply);
        var tracked = db.ClientCases.Local.Single(row => row.Id == source.Id); Assert.Equal(privateNote, legal ? tracked.LegalEntity!.Notes : tracked.Client!.Notes);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(request.Title, actual.Title); Assert.Equal(source.ClientId, actual.ClientId); Assert.Equal(source.LegalEntityId, actual.LegalEntityId); Assert.Equal(source.ResponsibleUserId, actual.ResponsibleUserId);
        await AssertEditLinksHistoryAsync(db, source, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseEditLinks_HttpPreservesUnchangedInactiveResponsible(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var assignee = await CreateUserAsync(db, "edit-old-inactive@example.local", "Прежний", DataVisibilityScope.All);
        await db.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false)); await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, assignee.Id));
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var request = EditRequest(source); request.Title = "Обычная правка";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(assignee.Id, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).ResponsibleUserId); await AssertEditLinksHistoryAsync(db, source, ids);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CaseEditLinks_HttpChecksVisibilityOnlyForANewOwner(bool legacy, bool legal, bool visible)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var outsider = await CreateUserAsync(db, "edit-new-owner@example.local", "Другой", DataVisibilityScope.All);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        int ownerId;
        if (legal) { var owner = new LegalEntity { ShortName = "Новый владелец", ResponsibleUserId = visible ? user.Id : outsider.Id }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); ownerId = owner.Id; }
        else { var owner = new Client { Name = "Новый владелец", ResponsibleUserId = visible ? user.Id : outsider.Id, IsActive = true }; db.Clients.Add(owner); await db.SaveChangesAsync(); ownerId = owner.Id; }
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var request = EditRequest(source); request.ClientId = legal ? null : ownerId; request.LegalEntityId = legal ? ownerId : null; request.Title = "С новым владельцем";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode); var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id);
        Assert.Equal(visible ? request.ClientId : source.ClientId, actual.ClientId); Assert.Equal(visible ? request.LegalEntityId : source.LegalEntityId, actual.LegalEntityId); Assert.Equal(visible ? request.Title : source.Title, actual.Title); await AssertEditLinksHistoryAsync(db, source, ids);
    }

    private static async Task AssertEditLinksHistoryAsync(PaymentContext db, ClientCase source, int[] ids)
    {
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(source.EventHistory, actual.EventHistory); Assert.Equal(source.CourtClaimAmount, actual.CourtClaimAmount);
        var tasks = await db.TaskJournalItems.AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal(2, tasks.Count(row => row.Status == "new")); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.All(tasks, task => Assert.Contains(task.ClientCaseId!.Value, ids)); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseEditLinks_HttpUsesFreshOwnerAndDepartmentVisibilityAfterAnEarlierChange(bool legacy, bool department)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var member = await CreateUserAsync(db, "edit-earlier-member@example.local", "Другой", DataVisibilityScope.All);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, department ? DataVisibilityScope.Department : DataVisibilityScope.Own));
        var target = new Client { Name = "Доступный до изменения владелец", IsActive = true, ResponsibleUserId = user.Id }; db.Clients.Add(target); await db.SaveChangesAsync();
        if (department)
        {
            var item = new DepartmentDictionaryItem { Name = "Синтетический отдел" }; db.Departments.Add(item); await db.SaveChangesAsync();
            await db.Users.Where(row => row.Id == user.Id || row.Id == member.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, item.Id).SetProperty(row => row.Department, item.Name));
            await db.Clients.Where(row => row.Id != target.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, member.Id));
            await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, member.Id));
        }
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var request = EditRequest(source); request.Title = "Не сохранять"; if (!department) request.ClientId = target.Id;
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var initial = await client.GetAsync(legacy ? $"/api/cases/{source.Id}" : $"/api/v2/cases/{source.Id}"); Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        if (department) await db.Users.Where(row => row.Id == member.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"));
        else await db.Clients.Where(row => row.Id == target.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, member.Id));
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(department ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertEditLinksHistoryAsync(db, source, ids);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public async Task CaseEditLinks_HttpRejectsAnInvalidExtraOwnerIdBeforeWriting(bool legacy, int invalid)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var request = EditRequest(source); request.LegalEntityId = invalid; request.Title = "Не сохранять";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }
}
