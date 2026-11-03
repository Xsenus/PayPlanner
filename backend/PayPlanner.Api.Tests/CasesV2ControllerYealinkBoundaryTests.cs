using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task<PromotionSeed> SeedYealinkAsync(PaymentContext db)
    {
        var seed = await SeedClientPromotionAsync(db);
        await db.Roles.Where(x => x.Id == seed.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "admin"));
        await db.Clients.Where(x => x.Id == seed.Client).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phone, "79001110001"));
        await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phone, "79001110002").SetProperty(x => x.PhonesJson, "[\"79001110002\"]"));
        db.ChangeTracker.Clear(); return seed;
    }
    private static FormFile YealinkFile(params (string Name, string Phone, string Extra)[] rows)
    {
        var xml = new XElement("YealinkIPPhoneDirectory", new XElement("Menu", new XAttribute("Name", "Тестовая группа"), rows.Select(x => new XElement("Unit", new XAttribute("Name", x.Name), new XAttribute("Phone1", x.Phone), new XAttribute("Phone2", x.Extra))))).ToString();
        var bytes = Encoding.UTF8.GetBytes(xml); return new(new MemoryStream(bytes), 0, bytes.Length, "file", "test.xml") { Headers = new HeaderDictionary(), ContentType = "application/xml" };
    }
    private static async Task<IActionResult?> RunYealinkAsync(PaymentContext db, PromotionSeed seed, string kind = "new-external", bool dryRun = false, CancellationToken ct = default, bool authenticated = true)
    {
        var phone = kind == "client" ? "79001110001" : kind == "external" ? "79001110002" : "79001110099";
        var decisions = kind == "new-client" ? "[{\"rowId\":1,\"action\":\"createClient\"}]" : kind is "client" or "external" ? "[{\"rowId\":1,\"action\":\"updateExisting\"}]" : null;
        return (await ClientContactController(db, seed.Actor, authenticated).ImportYealinkXml(YealinkFile(("Импортированное имя", phone, "79001110088"), ("Вторая строка", "79001110077", "")), dryRun, decisions, ct)).Result;
    }
    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("nonadmin")]
    public async Task YealinkBoundary_RequiresCurrentAuthenticatedApprovedDatabaseAdmin(string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db);
        if (state == "unapproved") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "nonadmin") await db.Roles.Where(x => x.Id == seed.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "lawyer"));
        if (state == "missing") seed = seed with { Actor = new User { Id = int.MaxValue } };
        var before = await PartySnapshotAsync(db); var result = await RunYealinkAsync(db, seed, authenticated: state != "unauthenticated"); Assert.True(result is UnauthorizedResult or ForbidResult, $"state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> YealinkActionCases() => from kind in new[] { "new-client", "new-external", "client", "external" } from postgres in new[] { false, true } select new object[] { kind, postgres };
    [Theory]
    [MemberData(nameof(YealinkActionCases))]
    public async Task YealinkBoundary_PreservesAllCreateAndUpdateDecisions(string kind, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db); var oldClient = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == seed.Client); var oldContact = await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == seed.Contact);
        var reply = Assert.IsType<YealinkContactImportResultDto>(Assert.IsType<OkObjectResult>(await RunYealinkAsync(db, seed, kind)).Value); Assert.False(reply.DryRun); Assert.Equal(2, reply.Scanned); Assert.Equal(kind is "client" or "external" ? 1 : 2, reply.Created); Assert.Equal(kind is "client" or "external" ? 1 : 0, reply.Updated); var first = reply.Details[0]; Assert.Equal(kind == "new-client" ? "CreatedClient" : kind == "new-external" ? "CreatedExternal" : "Updated", first.Action);
        if (kind == "client") { Assert.Equal(seed.Client, first.MatchedSourceId); var row = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == seed.Client); Assert.Equal(oldClient.Name, row.Name); Assert.Equal(oldClient.CreatedAt, row.CreatedAt); Assert.Equal(oldClient.BirthDate, row.BirthDate); Assert.Contains("79001110088", row.Notes); }
        if (kind == "external") { Assert.Equal(seed.Contact, first.MatchedSourceId); var row = await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == seed.Contact); Assert.Equal(oldContact.FullName, row.FullName); Assert.Equal(oldContact.CreatedAt, row.CreatedAt); Assert.Equal(oldContact.LegalEntityId, row.LegalEntityId); Assert.Equal(oldContact.ParentContactId, row.ParentContactId); Assert.True(row.IsActive && row.SyncToPhonebook); Assert.Contains("79001110088", row.PhonesJson); }
        if (kind == "new-client") { var row = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == first.MatchedSourceId); Assert.Equal(seed.Actor.Id, row.ResponsibleUserId); Assert.Contains("syncToPhonebook", row.Notes); Assert.Equal("Импортированное имя", row.Name); }
        Assert.All(await db.ClientCases.AsNoTracking().ToArrayAsync(), x => { Assert.Equal(123.45m, x.CourtClaimAmount); Assert.Equal("Исходная история", x.EventHistory); }); Assert.Equal("Сохранённый результат", await db.TaskJournalItems.Where(x => x.Status == "done").Select(x => x.Result).SingleAsync()); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task YealinkBoundary_DryRunAndSkippedRowsNeverWrite(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db); var before = await PartySnapshotAsync(db);
        var file = YealinkFile(("Есть", "79001110001", ""), ("Новый", "79001110099", ""), ("Повтор", "79001110099", ""), ("Без телефона", "", "")); var reply = Assert.IsType<YealinkContactImportResultDto>(Assert.IsType<OkObjectResult>((await ClientContactController(db, seed.Actor).ImportYealinkXml(file, true, "[{\"rowId\":1,\"action\":\"updateExisting\"}]", default)).Result).Value); Assert.True(reply.DryRun); Assert.Equal(1, reply.Created); Assert.Equal(2, reply.SkippedExisting); Assert.Equal(1, reply.SkippedNoPhone); Assert.Equal(0, reply.Updated); Assert.Equal("Existing", reply.Details[2].Action); Assert.Equal(before, await PartySnapshotAsync(db));
        var skipped = Assert.IsType<YealinkContactImportResultDto>(Assert.IsType<OkObjectResult>((await ClientContactController(db, seed.Actor).ImportYealinkXml(file, false, "[{\"rowId\":2,\"action\":\"skip\"},{\"rowId\":3,\"action\":\"skip\"}]", default)).Result).Value); Assert.Equal(2, skipped.SkippedByUser); Assert.Equal(0, skipped.Created); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> YealinkFailureCases() => from kind in new[] { "new-client", "new-external", "client", "external" } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { kind, postgres, cancel };
    [Theory]
    [MemberData(nameof(YealinkFailureCases))]
    public async Task YealinkBoundary_FailureAfterSecondRealSqlRollsBackWholeFile(string kind, bool postgres, bool cancel)
    {
        var fault = new YealinkAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault); var seed = await SeedYealinkAsync(db); var before = await PartySnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true; Task Write() => RunYealinkAsync(db, seed, kind, ct: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<YealinkSqlFault>(Write); Assert.Equal(2, fault.Calls); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task YealinkBoundary_DoesNotSaveOrClearForeignContext(bool pending, bool dryRun)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db); var before = await PartySnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа"; Assert.IsType<ConflictObjectResult>(await RunYealinkAsync(db, seed, dryRun: dryRun)); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task YealinkBoundary_HttpRechecksApprovalAndAdminAfterFilters(bool role, bool dryRun)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db); var before = await PartySnapshotAsync(db); using var server = ClientContactServer(db, async () => { if (role) await db.Roles.Where(x => x.Id == seed.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "lawyer")); else await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var body = new MultipartFormDataContent(); body.Add(new StringContent("<Root><Menu><Unit Name='HTTP' Phone1='79001110099'/></Menu></Root>"), "file", "test.xml"); using var response = await http.PostAsync("/api/contacts/imports/yealink?dryRun=" + dryRun.ToString().ToLowerInvariant(), body); Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP={(int)response.StatusCode}"); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("cancel")]
    [InlineData("cancel-during-read")]
    public async Task YealinkBoundary_InvalidAndCancelledFilesNeverWrite(string input)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedYealinkAsync(db); var before = await PartySnapshotAsync(db); using var cancellation = new CancellationTokenSource(); if (input == "cancel") cancellation.Cancel(); var bytes = Encoding.UTF8.GetBytes(input == "empty" ? "" : input == "malformed" ? "<Root>" : "<Root/>"); Stream stream = input == "cancel-during-read" ? new YealinkCancelStream(bytes, cancellation.Cancel) : new MemoryStream(bytes); var file = new FormFile(stream, 0, bytes.Length, "file", "test.xml"); if (input.StartsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientContactController(db, seed.Actor).ImportYealinkXml(file, false, null, cancellation.Token)); else Assert.IsType<BadRequestObjectResult>((await ClientContactController(db, seed.Actor).ImportYealinkXml(file, false, null, default)).Result); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    private sealed class YealinkCancelStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { cancel(); return ValueTask.FromCanceled<int>(ct); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) { cancel(); return Task.FromCanceled<int>(ct); }
    }
    private sealed class YealinkSqlFault : Exception;
    private sealed class YealinkAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Calls { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool write;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { write = Armed && data.Context!.ChangeTracker.Entries().Any(x => x.Entity is Client or ExternalContact && x.State is EntityState.Added or EntityState.Modified); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!write || ++Calls != 2) return result; Executed = await PartySnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new YealinkSqlFault(); }
    }
}
