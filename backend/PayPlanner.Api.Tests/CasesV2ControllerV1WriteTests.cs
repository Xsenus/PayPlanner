using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static Task<IActionResult> UpdateV1CaseAsync(PaymentContext db, User user, ClientCase source, ClientCase input, CancellationToken ct)
        => new CasesV1Controller(db) { ControllerContext = CreateController(db, user).ControllerContext }.Update(source.Id, input, ct);

    private static ClientCase V1CaseInput(ClientCase source, ClientCaseStatus? status = null)
        => new() { ClientId = source.ClientId, LegalEntityId = source.LegalEntityId, Title = source.Title, Description = source.Description, Status = status ?? source.Status };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteV1_AfterSqlFailureOrCancellationPreservesCaseTasksAndReport(bool postgres, bool cancel)
    {
        var fault = new ConfirmationAfterSaveFailure(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source, ClientCaseStatus.Closed); input.Title = "Не сохранять частично";
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => UpdateV1CaseAsync(db, user, source, input, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(fault.Called); db.ChangeTracker.Clear(); var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); var tasks = await db.TaskJournalItems.CountAsync();
        Assert.True(actual.Status == source.Status && actual.Title == source.Title && tasks == 3, $"status={actual.Status}; titleChanged={actual.Title != source.Title}; tasks={tasks}");
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteV1_PreservesForeignTransactionAndPendingWork(bool pending)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); var original = task.Result; if (pending) task.Result = "Чужой несохранённый результат V1";
        var input = V1CaseInput(source); input.Title = "Не сохранять"; Assert.IsType<ConflictObjectResult>(await UpdateV1CaseAsync(db, user, source, input, CancellationToken.None)); Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal(original, (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteV1_HttpKeepsUnchangedHiddenOwnerOfAnAssignedCase(bool legal)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var outsider = await CreateUserAsync(db, "v1-edit-owner@example.local", "Другой", DataVisibilityScope.All);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        if (legal)
        {
            var owner = new LegalEntity { ShortName = "Недоступная организация", ResponsibleUserId = outsider.Id }; db.LegalEntities.Add(owner); await db.SaveChangesAsync();
            await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, (int?)null).SetProperty(row => row.LegalEntityId, owner.Id));
        }
        else await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id));
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source); input.Title = "Правка назначенного дела";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}; assignedCaseWithUnchangedOwner=true"); var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id);
        Assert.Equal(input.Title, actual.Title); Assert.Equal(source.ClientId, actual.ClientId); Assert.Equal(source.LegalEntityId, actual.LegalEntityId); Assert.Equal(source.ResponsibleUserId, actual.ResponsibleUserId); await AssertEditLinksHistoryAsync(db, source, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteV1_HttpChecksAccessToANewOwner(bool legal, bool visible)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var outsider = await CreateUserAsync(db, "v1-edit-new-owner@example.local", "Другой", DataVisibilityScope.All);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own)); int nextId;
        if (legal) { var next = new LegalEntity { ShortName = "Новая организация", ResponsibleUserId = visible ? user.Id : outsider.Id }; db.LegalEntities.Add(next); await db.SaveChangesAsync(); nextId = next.Id; }
        else { var next = new Client { Name = "Новый клиент", IsActive = true, ResponsibleUserId = visible ? user.Id : outsider.Id }; db.Clients.Add(next); await db.SaveChangesAsync(); nextId = next.Id; }
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source); input.ClientId = legal ? null : nextId; input.LegalEntityId = legal ? nextId : null; input.Title = "Новый владелец";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode); var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id);
        Assert.Equal(visible ? input.Title : source.Title, actual.Title); Assert.Equal(visible ? input.ClientId : source.ClientId, actual.ClientId); Assert.Equal(visible ? input.LegalEntityId : source.LegalEntityId, actual.LegalEntityId); await AssertEditLinksHistoryAsync(db, source, ids);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("both")]
    [InlineData("neither")]
    public async Task CaseWriteV1_HttpRejectsInvalidOwnerPairsBeforeWriting(string mode)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source); input.Title = "Не сохранять";
        input.LegalEntityId = mode switch { "zero" => 0, "negative" => -1, "both" => 1234567, _ => null }; if (mode == "neither") input.ClientId = null;
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("unapproved", HttpStatusCode.Unauthorized)]
    [InlineData("view", HttpStatusCode.Forbidden)]
    [InlineData("edit", HttpStatusCode.Forbidden)]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    public async Task CaseWriteV1_HttpChecksCurrentActorPermissionsAndVisibility(string mode, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source); input.Title = "Не сохранять";
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "edit") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        if (mode == "hidden") { await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own)); await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); }
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync($"/api/v1/cases/{(mode == "missing" ? int.MaxValue : source.Id)}", input);
        Assert.Equal(expected, response.StatusCode); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteV1_HttpClosesReopensAndKeepsReplyOwnerBrief(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, ids) = await SeedBulkCasesAsync(db); const string privateNote = "Закрытые заметки владельца V1"; await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.Notes, privateNote)); db.ChangeTracker.Clear();
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source, ClientCaseStatus.Closed); input.Title = "Закрытое дело V1";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var closed = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input); Assert.Equal(HttpStatusCode.OK, closed.StatusCode); var reply = await closed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var owner = reply.GetProperty("client"); var leaks = owner.TryGetProperty("notes", out var notes) && notes.ValueKind == System.Text.Json.JsonValueKind.String && notes.GetString() == privateNote;
        Assert.True(!leaks, $"HTTP {(int)closed.StatusCode}; leakedOwnerNotes={leaks}"); Assert.Equal("Синтетический клиент", owner.GetProperty("name").GetString());
        var report = await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.IsCaseFinalReport); Assert.Equal("new", report.Status); Assert.Equal(user.Id, report.AssigneeUserId); Assert.Equal("done", (await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Title == "Открытая задача 1")).Status);
        input.Status = ClientCaseStatus.Open; using var reopened = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input); Assert.Equal(HttpStatusCode.OK, reopened.StatusCode); Assert.Equal("cancelled", (await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.IsCaseFinalReport)).Status);
        var tasks = await db.TaskJournalItems.AsNoTracking().ToArrayAsync(); Assert.Equal(4, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Title == "Завершённая задача").Result); Assert.Equal("new", Assert.Single(tasks, row => row.ClientCaseId == ids[1]).Status);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(ClientCaseStatus.Open, actual.Status); Assert.Equal(source.EventHistory, actual.EventHistory); Assert.Equal(source.CourtClaimAmount, actual.CourtClaimAmount); Assert.Equal(privateNote, (await db.Clients.AsNoTracking().SingleAsync()).Notes); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Fact]
    public async Task CaseWriteV1_MissingReportAssigneeDoesNotPartiallyCloseTasks()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await db.SystemSettings.ExecuteDeleteAsync(); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        Assert.IsType<BadRequestObjectResult>(await UpdateV1CaseAsync(db, user, source, V1CaseInput(source, ClientCaseStatus.Closed), CancellationToken.None)); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseWriteV1_HttpKeepsOnlyItsOriginalFiveWritableFields()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source);
        input.Title = "  Необрезанное название V1  "; input.Description = "Описание\r\nV1 "; input.ResponsibleUserId = int.MaxValue; input.FeeAmount = 9999m; input.CourtClaimAmount = 777m; input.EventHistory = "Не сохранять историю из V1"; input.CourtName = "Не менять суд";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(input.Title, actual.Title); Assert.Equal(input.Description, actual.Description); Assert.Equal(source.ResponsibleUserId, actual.ResponsibleUserId); Assert.Equal(source.FeeAmount, actual.FeeAmount); Assert.Equal(source.CourtName, actual.CourtName); await AssertEditLinksHistoryAsync(db, source, ids);
    }
}
