using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtMembershipReads()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var update in new[] { false, true })
                foreach (var tracked in new[] { false, true })
                    yield return [postgres, update, tracked];
    }

    [Theory]
    [MemberData(nameof(CourtMembershipReads))]
    public async Task CourtMembershipHttp_ReadResponseExcludesForeignRows(bool postgres, bool update, bool tracked)
    {
        var commands = new RecordingCommandInterceptor();
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(commands).Options)
            : await CreateContextAsync(commands);
        var seed = await SeedCourtMembershipAsync(db);
        var before = await CourtMembershipRowsAsync(db);
        if (!tracked) db.ChangeTracker.Clear();
        else await db.ClientCaseCourtComplaints.Include(row => row.CourtInstance).ToArrayAsync();
        commands.Commands.Clear();
        using var server = CourtCheckServer(db, seed.Actor, new CourtBindingProvider(() => Task.CompletedTask), NewCourtBindingNotifications(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", seed.Actor.Id.ToString());
        using var response = update
            ? await client.PutAsJsonAsync($"/api/v2/cases/{seed.Case.Id}/court-instances/first", new { })
            : await client.GetAsync($"/api/v2/cases/{seed.Case.Id}/court-instances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-other-case", json);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var instance = update ? JsonSerializer.Deserialize<CasesV2Controller.ClientCaseCourtInstanceDto>(json, options)!
            : JsonSerializer.Deserialize<CasesV2Controller.ClientCaseCourtInstanceDto[]>(json, options)!.Single(row => row.Instance == "first");
        Assert.Equal(new[] { seed.Newest.Id, seed.Oldest.Id }, instance.Complaints.Select(row => row.Id));
        Assert.All(instance.Complaints, row => { Assert.Equal(seed.Case.Id, row.ClientCaseId); Assert.Equal(seed.Instance.Id, row.CourtInstanceId); });
        if (!update) Assert.Single(commands.Commands, sql => sql.Contains("FROM \"ClientCaseCourtInstances\"", StringComparison.Ordinal));
        Assert.Equal(before, await CourtMembershipRowsAsync(db));
        using var hidden = await client.GetAsync($"/api/v2/cases/{seed.OtherCase.Id}/court-instances");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain("private-other-case", await hidden.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtMembershipHttp_ReadOtherSideExcludesOriginalCase(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtMembershipAsync(db);
        var before = await CourtMembershipRowsAsync(db);
        db.ChangeTracker.Clear();
        using var server = CourtCheckServer(db, seed.OtherActor, new CourtBindingProvider(() => Task.CompletedTask), NewCourtBindingNotifications(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", seed.OtherActor.Id.ToString());
        using var response = await client.GetAsync($"/api/v2/cases/{seed.OtherCase.Id}/court-instances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtInstanceDto[]>();
        var own = Assert.Single(rows!.Single(row => row.Instance == "first").Complaints);
        Assert.Equal("Жалоба другого дела", own.Title);
        Assert.Equal(seed.OtherCase.Id, own.ClientCaseId);
        Assert.Equal(before, await CourtMembershipRowsAsync(db));
        using var hidden = await client.GetAsync($"/api/v2/cases/{seed.Case.Id}/court-instances");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtMembershipHttp_RejectsForeignParentWithoutWrites(bool postgres, bool archive)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtMembershipAsync(db);
        var before = await CourtMembershipRowsAsync(db);
        var caseState = await CourtBindingStateAsync(db);
        using var server = CourtCheckServer(db, seed.Actor, new CourtBindingProvider(() => Task.CompletedTask), NewCourtBindingNotifications(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", seed.Actor.Id.ToString());
        var url = $"/api/v2/cases/{seed.Case.Id}/court-instances/first/complaints/{seed.WrongParent.Id}";
        using var response = archive ? await client.DeleteAsync(url)
            : await client.PutAsJsonAsync(url, new { title = "Подмена жалобы", filedBy = "unknown", status = "draft" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await CourtMembershipRowsAsync(db));
        Assert.Equal(caseState, await CourtBindingStateAsync(db));
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtMembership_ValidComplaintKeepsNormalUpdateAndArchive(bool postgres, bool archive)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtMembershipAsync(db);
        var badBefore = await db.ClientCaseCourtComplaints.AsNoTracking().Where(row => row.Id == seed.WrongParent.Id).Select(row => new { row.Title, row.Notes, row.IsArchived, row.CourtInstanceId }).SingleAsync();
        var controller = CreateController(db, seed.Actor);
        if (archive) Assert.IsType<NoContentResult>(await controller.ArchiveCourtComplaint(seed.Case.Id, "first", seed.Newest.Id, default));
        else
        {
            var result = await controller.UpdateCourtComplaint(seed.Case.Id, "first", seed.Newest.Id,
                new CasesV2Controller.UpsertClientCaseCourtComplaintRequest { Title = "Обновлённая жалоба", FiledBy = "unknown", Status = "draft" }, default);
            Assert.Equal("Обновлённая жалоба", Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Title);
        }
        var good = await db.ClientCaseCourtComplaints.AsNoTracking().SingleAsync(row => row.Id == seed.Newest.Id);
        Assert.Equal(archive, good.IsArchived);
        Assert.Equal(seed.Instance.Id, good.CourtInstanceId);
        Assert.Equal(seed.Case.Id, good.ClientCaseId);
        Assert.Equal(badBefore, await db.ClientCaseCourtComplaints.AsNoTracking().Where(row => row.Id == seed.WrongParent.Id).Select(row => new { row.Title, row.Notes, row.IsArchived, row.CourtInstanceId }).SingleAsync());
        Assert.Equal(7, await db.ClientCaseCourtComplaints.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtMembershipHttp_ReadRequiresViewAndAllowsViewOnly(bool postgres, bool denyView)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtMembershipAsync(db);
        var permission = await db.RolePermissions.SingleAsync(row => row.RoleId == seed.Actor.RoleId);
        permission.CanView = !denyView;
        permission.CanEdit = denyView;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CourtMembershipRowsAsync(db);
        using var server = CourtCheckServer(db, seed.Actor, new CourtBindingProvider(() => Task.CompletedTask), NewCourtBindingNotifications(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", seed.Actor.Id.ToString());
        using var response = await client.GetAsync($"/api/v2/cases/{seed.Case.Id}/court-instances");
        Assert.Equal(denyView ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        if (!denyView)
        {
            var rows = await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtInstanceDto[]>();
            Assert.Equal(2, rows!.Single(row => row.Instance == "first").Complaints.Count);
            using var blocked = await client.PutAsJsonAsync($"/api/v2/cases/{seed.Case.Id}/court-instances/first", new { });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }
        Assert.Equal(before, await CourtMembershipRowsAsync(db));
    }

    [Fact]
    public void CourtMembership_MapperRejectsBothWrongParentKeysAndArchive()
    {
        var instance = new ClientCaseCourtInstance { Id = 7, ClientCaseId = 11, Instance = "first" };
        instance.Complaints = [
            new() { Id = 1, ClientCaseId = 11, CourtInstanceId = 7, Title = "Старшая", CreatedAt = DateTime.UnixEpoch },
            new() { Id = 2, ClientCaseId = 11, CourtInstanceId = 7, Title = "Новая", CreatedAt = DateTime.UnixEpoch.AddDays(1) },
            new() { Id = 3, ClientCaseId = 12, CourtInstanceId = 7, Title = "Чужое дело" },
            new() { Id = 4, ClientCaseId = 11, CourtInstanceId = 8, Title = "Чужая инстанция" },
            new() { Id = 5, ClientCaseId = 11, CourtInstanceId = 7, Title = "Архивная", IsArchived = true }
        ];
        var mapper = typeof(CasesV2Controller).GetMethod("MapCourtInstance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mapped = Assert.IsType<CasesV2Controller.ClientCaseCourtInstanceDto>(mapper.Invoke(null, [instance]));
        Assert.Equal(new[] { 2, 1 }, mapped.Complaints.Select(row => row.Id));
        Assert.Equal(5, instance.Complaints.Count);
    }

    private sealed record CourtMembershipSeed(User Actor, User OtherActor, ClientCase Case, ClientCase OtherCase,
        ClientCaseCourtInstance Instance, ClientCaseCourtComplaint Newest, ClientCaseCourtComplaint Oldest, ClientCaseCourtComplaint WrongParent);

    private static async Task<CourtMembershipSeed> SeedCourtMembershipAsync(PaymentContext db)
    {
        var (actor, clientCase) = await SeedCourtSearchAsync(db);
        var otherActor = await CreateUserAsync(db, "complaint-other@synthetic.local", "Другой сотрудник");
        var otherCase = new ClientCase
        {
            Title = "Чужое дело",
            ResponsibleUserId = otherActor.Id,
            Client = new Client { Name = "Чужой клиент", IsActive = true, ResponsibleUserId = otherActor.Id }
        };
        var first = new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "first", CourtName = "Свой суд" };
        var appeal = new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "appeal" };
        var other = new ClientCaseCourtInstance { ClientCase = otherCase, Instance = "first", CourtName = "Другой суд" };
        db.ClientCases.Add(otherCase); db.ClientCaseCourtInstances.AddRange(first, appeal, other);
        await db.SaveChangesAsync();
        var oldest = new ClientCaseCourtComplaint
        { ClientCaseId = clientCase.Id, CourtInstanceId = first.Id, Title = "Старая жалоба", Notes = "Своя заметка", CreatedAt = DateTime.UnixEpoch, FiledAt = DateTime.UnixEpoch };
        var newest = new ClientCaseCourtComplaint
        { ClientCaseId = clientCase.Id, CourtInstanceId = first.Id, Title = "Новая жалоба", CreatedAt = DateTime.UnixEpoch.AddDays(1), FiledAt = DateTime.UnixEpoch.AddDays(1) };
        var wrongParent = new ClientCaseCourtComplaint
        { ClientCaseId = clientCase.Id, CourtInstanceId = other.Id, Title = "Своя жалоба в чужой инстанции", Notes = "private-original-case" };
        db.ClientCaseCourtComplaints.AddRange(oldest, newest, wrongParent,
            new ClientCaseCourtComplaint { ClientCaseId = clientCase.Id, CourtInstanceId = first.Id, Title = "Архивная", IsArchived = true },
            new ClientCaseCourtComplaint { ClientCaseId = clientCase.Id, CourtInstanceId = appeal.Id, Title = "Апелляционная" },
            new ClientCaseCourtComplaint { ClientCaseId = otherCase.Id, CourtInstanceId = first.Id, Title = "Чужая в своей инстанции", Notes = "private-other-case" },
            new ClientCaseCourtComplaint { ClientCaseId = otherCase.Id, CourtInstanceId = other.Id, Title = "Жалоба другого дела" });
        await db.SaveChangesAsync();
        return new(actor, otherActor, clientCase, otherCase, first, newest, oldest, wrongParent);
    }

    private static async Task<string> CourtMembershipRowsAsync(PaymentContext db) => JsonSerializer.Serialize(
        await db.ClientCaseCourtComplaints.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.ClientCaseId,
            row.CourtInstanceId,
            row.ComplaintType,
            row.Title,
            row.TargetInstance,
            row.FiledAt,
            row.FiledBy,
            row.Status,
            row.ClientCaseDocumentId,
            row.Notes,
            row.IsArchived,
            row.CreatedAt,
            row.CreatedByUserId,
            row.CreatedByUserName,
            row.UpdatedAt,
            row.UpdatedByUserId,
            row.UpdatedByUserName
        }).ToArrayAsync());
}
