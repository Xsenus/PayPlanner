using System.Security.Claims;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task<(User Actor, int[] Cases, int First, int Second)> SeedClientBatchAsync(PaymentContext db)
    {
        var (actor, cases, _) = await SeedBirthdayClientAsync(db);
        var first = new Client { Name = "Удаляемый1", ResponsibleUserId = actor.Id }; var second = new Client { Name = "Удаляемый2", ResponsibleUserId = actor.Id };
        db.Clients.AddRange(first, second); await db.SaveChangesAsync();
        foreach (var client in new[] { first, second })
        {
            db.AddressRecords.Add(new AddressRecord { ClientId = client.Id, Value = "Синтетический адрес", IsPrimary = true });
            db.ClientPassports.Add(new ClientPassport { ClientId = client.Id, Series = "TEST", Number = "123456", IsPrimary = true });
            db.ExternalContacts.Add(new ExternalContact { LinkedClientId = client.Id, FullName = client.Name });
        }
        await db.SaveChangesAsync(); return (actor, cases, first.Id, second.Id);
    }

    private static Task<IActionResult> WriteClientBatchAsync(PaymentContext db, User actor, int[] ids, int operation, int? assignee = null, CancellationToken ct = default, bool authenticated = true)
    {
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id);
        if (!authenticated) root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(root.User.Claims));
        return operation switch
        {
            0 => root.Delete(ids[0], ct),
            1 => new ClientsV1Controller(db) { ControllerContext = root.ControllerContext }.Delete(ids[0], ct),
            2 => new ClientsV2Controller(db) { ControllerContext = root.ControllerContext }.Delete(ids[0], ct),
            3 => root.BulkDelete(new BulkIdsRequest { Ids = ids.ToList() }, ct),
            _ => root.ChangeResponsible(new BulkAssignResponsibleRequest { Ids = ids.ToList(), ResponsibleUserId = assignee ?? actor.Id }, ct),
        };
    }

    private static async Task<string> ClientBatchSnapshotAsync(PaymentContext db)
    {
        var snapshot = new
        {
            Clients = await db.Clients.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Name, row.ResponsibleUserId, row.BirthDate, row.Notes, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
            Addresses = await db.AddressRecords.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.Value, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
            Passports = await db.ClientPassports.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.Series, row.Number, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
            Contacts = await db.ExternalContacts.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.LinkedClientId }).ToArrayAsync(),
            Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.ExpenseClientId, row.ClientCaseId, row.Amount, row.PaidAmount, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
            Cases = await db.ClientCases.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.Status, row.CourtClaimAmount, row.EventHistory, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
            Tasks = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.ClientCaseId, row.Status, row.Result, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
        };
        return JsonSerializer.Serialize(snapshot);
    }

    public static IEnumerable<object[]> ClientBatchAdmissionCases()
        => from operation in new[] { 0, 1, 2, 3, 4 } from state in new[] { "unapproved", "inactive", "missing", "denied-view", "denied-write", "unauthenticated" } select new object[] { operation, state };

    [Theory]
    [MemberData(nameof(ClientBatchAdmissionCases))]
    public async Task ClientBatch_RequiresFreshApprovedActorAndCurrentAction(int operation, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, _, first, second) = await SeedClientBatchAsync(db); var before = await ClientBatchSnapshotAsync(db);
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state == "denied-view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state == "denied-write") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanDelete, false).SetProperty(row => row.CanEdit, false));
        var result = await WriteClientBatchAsync(db, actor, [first, second], operation, authenticated: state != "unauthenticated");
        Assert.True(result is ForbidResult or UnauthorizedResult, $"operation={operation}; state={state}; result={result.GetType().Name}"); Assert.Equal(before, await ClientBatchSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> ClientBatchContextCases()
        => from operation in new[] { 0, 1, 2, 3, 4 } from pending in new[] { false, true } select new object[] { operation, pending };

    [Theory]
    [MemberData(nameof(ClientBatchContextCases))]
    public async Task ClientBatch_PreservesForeignTransactionOrPendingWork(int operation, bool pending)
    {
        await using var db = await CreateContextAsync(); var (actor, _, first, second) = await SeedClientBatchAsync(db); var before = await ClientBatchSnapshotAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужие изменения";
        Assert.IsType<ConflictObjectResult>(await WriteClientBatchAsync(db, actor, [first, second], operation));
        Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужие изменения", task.Result); }
        Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    public static IEnumerable<object[]> ClientDeletionReferenceCases()
    {
        foreach (var kind in new[] { "payment", "expense", "act", "case", "contract", "power", "task", "request", "balance", "cash", "party", "portal", "binding", "delivery", "signature", "deleted-payment", "deleted-task", "deleted-case" }) yield return new object[] { 0, kind };
        foreach (var operation in new[] { 1, 2 })
            foreach (var kind in new[] { "payment", "expense", "case", "task", "deleted-payment" }) yield return new object[] { operation, kind };
        foreach (var kind in new[] { "request", "balance", "cash", "signature", "deleted-case" }) yield return new object[] { 3, kind };
    }

    private static async Task AddClientDeletionReferenceAsync(PaymentContext db, User actor, int[] cases, int client, string kind)
    {
        object reference;
        switch (kind)
        {
            case "payment": case "expense": case "deleted-payment": reference = new Payment { ClientId = kind == "expense" ? null : client, ExpenseClientId = kind == "expense" ? client : null, ClientCaseId = cases[0], Amount = 123.45m, PaidAmount = 12m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow }; break;
            case "act": reference = new Act { ClientId = client, Number = "TEST-A", Date = DateTime.UtcNow, Amount = 123.45m }; break;
            case "case": case "deleted-case": reference = new ClientCase { ClientId = client, Title = "Связанное дело", CourtClaimAmount = 123.45m }; break;
            case "contract": var contract = new Contract { Number = "TEST-C", Date = DateTime.UtcNow }; db.Contracts.Add(contract); await db.SaveChangesAsync(); reference = new ClientContract { ClientId = client, ContractId = contract.Id }; break;
            case "power": reference = new PowerOfAttorney { ClientId = client, Number = "TEST-P", IssuedAt = DateTime.UtcNow }; break;
            case "task": case "deleted-task": reference = new TaskJournalItem { ClientId = client, Title = "Самостоятельная задача", Status = "done", Result = "Сохранённый результат", TagsJson = "[]" }; break;
            case "request": var status = new SalesRequestStatus { Key = "synthetic", Title = "Синтетический" }; db.SalesRequestStatuses.Add(status); await db.SaveChangesAsync(); reference = new SalesRequest { ClientId = client, StatusId = status.Id, Summary = "История обращения", ResponsibleUserId = actor.Id, CreatedByUserId = actor.Id }; break;
            case "balance": reference = new ClientBalanceTransaction { ClientId = client, Amount = 123.45m, Description = "История остатка" }; break;
            case "receipt": reference = new BalanceApplicationReceipt { ClientId = client, ActorUserId = actor.Id, RequestId = Guid.NewGuid(), PaymentId = 12345, CommandSha256 = new string('a', 64), AppliedAmount = 12m, Balance = 123.45m, CreatedAtUtc = DateTime.UtcNow }; break;
            case "cash": reference = new CashOrder { ClientId = client, Number = "TEST-CASH", Amount = 123.45m, Date = DateTime.UtcNow, Status = CashOrderStatus.Draft }; break;
            case "party": reference = new ClientCaseRelatedParty { ClientId = client, ClientCaseId = cases[0], DisplayName = "Участник дела" }; break;
            case "portal": reference = new ClientPortalAccount { ClientId = client, Email = "portal@example.local", DisplayName = "Доступ клиента" }; break;
            case "binding": reference = new MessengerRecipientBinding { ClientId = client, Channel = "Max", Provider = "synthetic", DisplayName = "Синтетический", ProtectedAddress = "isolated" }; break;
            case "delivery": var binding = new MessengerRecipientBinding { ClientId = await db.ClientCases.Where(row => row.Id == cases[0]).Select(row => row.ClientId).SingleAsync(), Channel = "Max", Provider = "synthetic", DisplayName = "Синтетический", ProtectedAddress = "isolated" }; db.MessengerRecipientBindings.Add(binding); await db.SaveChangesAsync(); reference = new MessengerDocumentDelivery { ClientId = client, RecipientBindingId = binding.Id, CreatedByUserId = actor.Id, DocumentType = "Invoice", DocumentId = 12345, OriginalFileName = "synthetic.pdf", SnapshotPath = "isolated/synthetic.pdf", Channel = "Max", Provider = "synthetic", IdempotencyKey = Guid.NewGuid().ToString(), ChecksumSha256 = new string('a', 64) }; break;
            default: reference = new ElectronicSignatureEnvelope { ClientId = client, CreatedByUserId = actor.Id, DocumentType = "Invoice", DocumentId = 12345, IdempotencyKey = Guid.NewGuid().ToString(), Provider = "synthetic", SnapshotPath = "isolated/synthetic.pdf" }; break;
        }
        db.Add(reference); await db.SaveChangesAsync(); if (kind.StartsWith("deleted-")) { db.Remove(reference); await db.SaveChangesAsync(); }
    }

    [Theory]
    [MemberData(nameof(ClientDeletionReferenceCases))]
    public async Task ClientBatch_DeletionKeepsEveryBusinessReferenceAndDeletedHistory(int operation, string kind)
    {
        await using var db = await CreateContextAsync(); var (actor, cases, first, second) = await SeedClientBatchAsync(db);
        await AddClientDeletionReferenceAsync(db, actor, cases, first, kind); var before = await ClientBatchSnapshotAsync(db);
        IActionResult? result = null; Exception? error = null; try { result = await WriteClientBatchAsync(db, actor, [first, second], operation); } catch (Exception caught) { error = caught; }
        var after = await ClientBatchSnapshotAsync(db);
        Assert.True(result is ConflictObjectResult && error is null && before == after, $"operation={operation}; kind={kind}; result={result?.GetType().Name}; error={error?.GetType().Name}; originalGraphPreserved={before == after}");
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ClientBatch_ReceiptHistoryAlonePreventsDeletionWithoutRewritingOutcome()
    {
        await using var db = await CreateContextAsync(); var (actor, cases, first, second) = await SeedClientBatchAsync(db);
        await AddClientDeletionReferenceAsync(db, actor, cases, first, "receipt"); var before = await ClientBatchSnapshotAsync(db);
        var receipt = await db.BalanceApplicationReceipts.AsNoTracking().SingleAsync();
        var result = await WriteClientBatchAsync(db, actor, [first, second], 0);
        Assert.True(result is ConflictObjectResult && before == await ClientBatchSnapshotAsync(db), $"result={result.GetType().Name}; graphPreserved={before == await ClientBatchSnapshotAsync(db)}");
        var after = await db.BalanceApplicationReceipts.AsNoTracking().SingleAsync(); Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(after));
    }

    public static IEnumerable<object[]> ClientBatchFailureCases()
        => from operation in new[] { 0, 1, 2, 3, 4 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { operation, postgres, cancel };

    [Theory]
    [MemberData(nameof(ClientBatchFailureCases))]
    public async Task ClientBatch_RollsBackExecutedDeletionOrAssignmentAndProfile(int operation, bool postgres, bool cancel)
    {
        var fault = new ClientBatchAfterSqlFailure(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, _, first, second) = await SeedClientBatchAsync(db); var other = await CreateUserAsync(db, "batch-assignee@example.local", "Новый ответственный"); var before = await ClientBatchSnapshotAsync(db);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => WriteClientBatchAsync(db, actor, [first, second], operation, other.Id, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ClientBatchSqlFault>(Write);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.ExecutedSnapshot); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    public static IEnumerable<object[]> ClientBatchSuccessCases()
        => from operation in new[] { 0, 1, 2, 3, 4 } from postgres in new[] { false, true } select new object[] { operation, postgres };

    [Theory]
    [MemberData(nameof(ClientBatchSuccessCases))]
    public async Task ClientBatch_DeletesOnlyUnusedProfilesOrAssignsSelectedClients(int operation, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, first, second) = await SeedClientBatchAsync(db); var other = await CreateUserAsync(db, "batch-success@example.local", "Новый ответственный");
        var result = await WriteClientBatchAsync(db, actor, [first, second], operation, other.Id);
        if (operation < 3) Assert.IsType<NoContentResult>(result); else Assert.IsType<OkObjectResult>(result);
        if (operation == 4)
        {
            Assert.All(await db.Clients.AsNoTracking().Where(row => row.Id == first || row.Id == second).ToArrayAsync(), row => Assert.Equal(other.Id, row.ResponsibleUserId));
            Assert.Equal(2, await db.ExternalContacts.CountAsync(row => row.LinkedClientId == first || row.LinkedClientId == second));
        }
        else
        {
            int[] removed = operation == 3 ? [first, second] : [first];
            Assert.Equal(0, await db.Clients.CountAsync(row => removed.Contains(row.Id))); Assert.Equal(removed.Length, await db.Clients.IgnoreQueryFilters().CountAsync(row => removed.Contains(row.Id) && EF.Property<bool>(row, "IsDeleted")));
            Assert.Equal(0, await db.AddressRecords.CountAsync(row => row.ClientId.HasValue && removed.Contains(row.ClientId.Value))); Assert.Equal(0, await db.ClientPassports.CountAsync(row => removed.Contains(row.ClientId))); Assert.Equal(0, await db.ExternalContacts.CountAsync(row => row.LinkedClientId.HasValue && removed.Contains(row.LinkedClientId.Value)));
            Assert.Equal(operation == 3 ? 0 : 1, await db.Clients.CountAsync(row => row.Id == second));
        }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task ClientBatch_MixedMissingOrHiddenSelectionMakesNoChanges(int operation, bool hidden)
    {
        await using var db = await CreateContextAsync(); var (actor, _, first, second) = await SeedClientBatchAsync(db); var other = await CreateUserAsync(db, "batch-hidden@example.local", "Другой сотрудник");
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        if (hidden) await db.Clients.Where(row => row.Id == second).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        var before = await ClientBatchSnapshotAsync(db); Assert.IsType<NotFoundObjectResult>(await WriteClientBatchAsync(db, actor, [first, hidden ? second : int.MaxValue], operation)); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("missing")]
    [InlineData("inactive")]
    public async Task ClientBatch_AssigneeBoundaryRejectsInvalidTargetWithoutPartialChanges(string kind)
    {
        await using var db = await CreateContextAsync(); var (actor, _, first, second) = await SeedClientBatchAsync(db); var other = await CreateUserAsync(db, "batch-invalid@example.local", "Ответственный");
        await db.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false)); var before = await ClientBatchSnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteClientBatchAsync(db, actor, [first, second], 4, kind == "zero" ? 0 : kind == "missing" ? int.MaxValue : other.Id)); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ClientBatch_EmptySelectionReturnsValidationError(int operation)
    {
        await using var db = await CreateContextAsync(); var (actor, _, _, _) = await SeedClientBatchAsync(db); var before = await ClientBatchSnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteClientBatchAsync(db, actor, [], operation)); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    public static IEnumerable<object[]> ClientBatchHttpCases()
    {
        foreach (var operation in new[] { 0, 1, 2, 3, 4 }) yield return new object[] { operation, false };
        foreach (var operation in new[] { 0, 1, 2, 3 }) yield return new object[] { operation, true };
    }

    [Theory]
    [MemberData(nameof(ClientBatchHttpCases))]
    public async Task ClientBatch_HttpEnforcesCurrentApprovalAndKeepsUsedClientLinks(int operation, bool used)
    {
        await using var db = await CreateContextAsync(); var (actor, cases, first, second) = await SeedClientBatchAsync(db);
        if (used) await AddClientDeletionReferenceAsync(db, actor, cases, first, "payment");
        else await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        var before = await ClientBatchSnapshotAsync(db); using var server = CaseWriteServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = operation switch
        {
            0 => await http.DeleteAsync($"/api/clients/{first}"),
            1 => await http.DeleteAsync($"/api/v1/clients/{first}"),
            2 => await http.DeleteAsync($"/api/v2/clients/{first}"),
            3 => await http.PostAsJsonAsync("/api/clients/bulk-delete", new BulkIdsRequest { Ids = [first, second] }),
            _ => await http.PostAsJsonAsync("/api/clients/change-responsible", new BulkAssignResponsibleRequest { Ids = [first, second], ResponsibleUserId = actor.Id }),
        };
        Assert.True(used ? response.StatusCode == HttpStatusCode.Conflict : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"operation={operation}; used={used}; HTTP={(int)response.StatusCode}; graphPreserved={before == await ClientBatchSnapshotAsync(db)}");
        Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Fact]
    public async Task ClientBatch_ReferenceInventoryCoversCurrentClientForeignKeys()
    {
        await using var db = await CreateContextAsync();
        var actual = db.Model.FindEntityType(typeof(Client))!.GetReferencingForeignKeys().Select(fk => fk.DeclaringEntityType.ClrType.Name + "." + string.Join(',', fk.Properties.Select(p => p.Name))).Order().ToArray();
        string[] expected = ["Act.ClientId", "AddressRecord.ClientId", "CashOrder.ClientId", "ClientBalanceTransaction.ClientId", "ClientCase.ClientId", "ClientCaseRelatedParty.ClientId", "ClientContract.ClientId", "ClientPassport.ClientId", "ClientPortalAccount.ClientId", "ElectronicSignatureEnvelope.ClientId", "ExternalContact.LinkedClientId", "MessengerDocumentDelivery.ClientId", "MessengerRecipientBinding.ClientId", "Payment.ClientId", "Payment.ExpenseClientId", "PowerOfAttorney.ClientId", "TaskJournalItem.ClientId"];
        Assert.Equal(expected.Order(), actual);
    }

    private sealed class ClientBatchSqlFault : Exception;
    private sealed class ClientBatchAfterSqlFailure(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? ExecutedSnapshot { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            fail = Armed && eventData.Context!.ChangeTracker.Entries<Client>().Any(entry => entry.State == EntityState.Modified || entry.State == EntityState.Deleted);
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!fail) return result; Called = true; ExecutedSnapshot = await ClientBatchSnapshotAsync((PaymentContext)eventData.Context!);
            if (cancel) { Cancel!(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new ClientBatchSqlFault();
        }
    }
}
