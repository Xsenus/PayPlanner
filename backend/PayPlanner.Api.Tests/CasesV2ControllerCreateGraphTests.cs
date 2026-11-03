using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Fact]
    public async Task CaseCreateGraph_V1CannotCreateNestedPaymentOrClientAndKeepsOrdinaryCreation()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        foreach (var payment in new[] { true, false })
        {
            var input = new ClientCase { ClientId = owner, Title = "Дело V1" };
            if (payment) input.Payments = [new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }];
            else input.Client = new Client { Name = "Вложенный клиент", IsActive = true };
            using var rejected = await client.PostAsJsonAsync("/api/v1/cases", input); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Contains("отдельно", await rejected.Content.ReadAsStringAsync()); await AssertBulkCasesUnchangedAsync(db, ids);
        }
        var entity = await db.Clients.SingleAsync(row => row.Id == owner); entity.Notes = "Закрытые заметки V1"; await db.SaveChangesAsync();
        using var created = await client.PostAsJsonAsync("/api/v1/cases", new ClientCase { ClientId = owner, Title = "Обычное дело V1" }); Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.DoesNotContain(entity.Notes, await created.Content.ReadAsStringAsync()); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(3, await db.ClientCases.CountAsync()); Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCreateGraph_HttpCannotCreatePaymentWithoutPaymentPermissionOrOperationKey(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db);
        var owner = await db.ClientCases.AsNoTracking().Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var model = new ClientCase
        {
            ClientId = owner,
            Title = "Дело с вложенным платежом",
            Status = ClientCaseStatus.Open,
            Payments = [new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }]
        };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/{(legacy ? "cases" : "v2/cases")}", model);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync());
        Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseCreateGraph_PgRejectsNestedPaymentEvenWithPaymentAccessAndAKey(bool legacy, bool financialAccess)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (user, ids) = await SeedBulkCasesAsync(db); if (financialAccess) await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var model = new ClientCase { ClientId = owner, Title = "Вложенный платёж", Payments = [new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"/api/{(legacy ? "cases" : "v2/cases")}", model);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync());
        db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync()); Assert.Equal(1, await db.Users.CountAsync()); Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCreateGraph_EveryMappedRelationIsRejectedBeforeAttachingAnyRecords(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var navigations = db.Model.FindEntityType(typeof(ClientCase))!.GetNavigations().ToArray(); Assert.Equal(18, navigations.Length);
        foreach (var navigation in navigations)
        {
            var model = new ClientCase { ClientId = owner, Title = "Запрещённая связь " + navigation.Name };
            var related = Activator.CreateInstance(navigation.TargetEntityType.ClrType)!;
            if (navigation.IsCollection) { var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(navigation.TargetEntityType.ClrType))!; list.Add(related); navigation.PropertyInfo!.SetValue(model, list); }
            else navigation.PropertyInfo!.SetValue(model, related);
            var publisher = new BulkPublicationBoundary(db); var controller = CreateController(db, user, realtimePublisher: publisher);
            var result = legacy ? await new CasesController(db, publisher) { ControllerContext = controller.ControllerContext }.Create(model, CancellationToken.None) : await controller.Create(model, CancellationToken.None);
            var rejected = Assert.IsType<BadRequestObjectResult>(result); Assert.Contains("отдельно", JsonSerializer.SerializeToElement(rejected.Value).GetProperty("message").GetString());
            Assert.Equal(0, publisher.Count); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
        }
        Assert.Equal(1, await db.Clients.CountAsync()); Assert.Equal(1, await db.Users.CountAsync()); Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseCreateGraph_HttpKeepsOrdinaryCreationAndPrivateOwnerDataSeparate(bool legacy, bool legal)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db);
        const string note = "Закрытая заметка владельца нового дела"; const string caption = "Владелец нового дела";
        int owner;
        if (legal) { var entity = new LegalEntity { ShortName = caption, Notes = note, ResponsibleUserId = user.Id }; db.LegalEntities.Add(entity); await db.SaveChangesAsync(); owner = entity.Id; }
        else { owner = (await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync())!.Value; var entity = await db.Clients.SingleAsync(row => row.Id == owner); entity.Name = caption; entity.Notes = note; await db.SaveChangesAsync(); }
        var model = new ClientCase { ClientId = legal ? null : owner, LegalEntityId = legal ? owner : null, ResponsibleUserId = user.Id, Title = "Новое обычное дело", CourtClaimAmount = 123.45m, CourtInstanceKeys = ["first"] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/{(legacy ? "cases" : "v2/cases")}", model);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(note, body); Assert.Contains(caption, body);
        var saved = await response.Content.ReadFromJsonAsync<ClientCase>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }); Assert.NotNull(saved); Assert.True(saved.Id > 0); Assert.Equal(model.Title, saved.Title); Assert.Equal(model.ClientId, saved.ClientId); Assert.Equal(model.LegalEntityId, saved.LegalEntityId); Assert.Equal(user.Id, saved.ResponsibleUserId); Assert.Equal(123.45m, saved.CourtClaimAmount);
        Assert.EndsWith($"/{saved.Id}", response.Headers.Location!.ToString()); Assert.Equal(3, await db.ClientCases.CountAsync()); Assert.Equal(1, await db.Users.CountAsync()); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(3, await db.TaskJournalItems.CountAsync());
        Assert.Equal(note, legal ? await db.LegalEntities.Where(row => row.Id == owner).Select(row => row.Notes).SingleAsync() : await db.Clients.Where(row => row.Id == owner).Select(row => row.Notes).SingleAsync());
    }
}
