using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Cases;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CaseWriteMergeCases()
    {
        foreach (var legacy in new[] { false, true })
            foreach (var field in new[] { "history", "title", "strategy", "status" })
                foreach (var edited in new[] { false, true }) yield return [legacy, field, edited];
    }

    [Theory]
    [MemberData(nameof(CaseWriteMergeCases))]
    public async Task CaseWriteHttp_ConflictingEditsFailAndUntouchedFieldsPreserveNewValues(bool legacy, string field, bool edited)
    {
        await using var db = await CreateContextAsync();
        var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source);
        request.Description = "Моя новая заметка";
        if (field == "history")
        {
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.EventHistory, "Новая запись сотрудника"));
            if (edited) request.EventHistory = "Моя правка истории";
        }
        else if (field == "title")
        {
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Новое название сотрудника"));
            if (edited) request.Title = "Моё название";
        }
        else if (field == "strategy")
        {
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Strategy, "Новая позиция сотрудника"));
            if (edited) request.Strategy = "Моя позиция";
        }
        else
        {
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ClientCaseStatus.OnHold));
            if (edited) request.Status = ClientCaseStatus.Lost;
        }
        var before = await db.ClientCases.AsNoTracking().SingleAsync();
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(edited ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(before.Title, actual.Title); Assert.Equal(before.Strategy, actual.Strategy);
        Assert.Equal(before.Status, actual.Status); Assert.Equal(before.EventHistory, actual.EventHistory);
        Assert.Equal(edited ? before.Description : request.Description, actual.Description);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
        if (!edited)
        {
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(actual.EventHistory, reply.GetProperty("eventHistory").GetString());
        }
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteHttp_BothRoutesRequireOriginalSource(bool legacy, bool oldRoute)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy, oldRoute), new { title = "Нельзя без источника" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync()).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteHttp_MovedOwnerIsNotSilentlyAdopted(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Title = "Моя правка";
        var other = new Client { Name = "Другой клиент", IsActive = true, ResponsibleUserId = user.Id }; db.Clients.Add(other); await db.SaveChangesAsync();
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, other.Id));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(source.Title, actual.Title); Assert.Equal(other.Id, actual.ClientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteHttp_ClearFlagCannotEraseConcurrentCourtAmount(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.ClearCourtClaimAmount = true;
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtClaimAmount, 999m));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(999m, (await db.ClientCases.AsNoTracking().SingleAsync()).CourtClaimAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteHttp_OriginallyEmptyClearFlagDoesNotEraseNewDate(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Description = "Моя заметка"; request.ClearCourtRegisteredAt = true;
        var changedAt = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtRegisteredAt, changedAt));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(changedAt, actual.CourtRegisteredAt); Assert.Equal(request.Description, actual.Description);
    }

    [Fact]
    public async Task CaseWriteHttp_MaskedFeeIsPreservedAndReplyDoesNotRevealIt()
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Title = "Моя правка"; request.FeeAmount = 0m; request.ExpectedSource!.FeeAmount = 0m;
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FeeAmount, 150m));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, false), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(150m, (await db.ClientCases.AsNoTracking().SingleAsync()).FeeAmount);
        Assert.Equal(0m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("feeAmount").GetDecimal());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    public static IEnumerable<object[]> CaseWriteAccessCases()
    {
        foreach (var legacy in new[] { false, true })
            foreach (var mode in new[] { "anonymous", "inactive", "unapproved", "permission", "hidden", "missing" }) yield return [legacy, mode];
    }

    [Theory]
    [MemberData(nameof(CaseWriteAccessCases))]
    public async Task CaseWriteHttp_CurrentPermissionAndVisibilityCannotBeBypassed(bool legacy, string mode)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Title = "Запрещённая правка";
        var expected = HttpStatusCode.Unauthorized;
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        else if (mode == "permission")
        {
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false)); expected = HttpStatusCode.Forbidden;
        }
        else if (mode == "hidden")
        {
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); expected = HttpStatusCode.NotFound;
        }
        else if (mode == "missing") expected = HttpStatusCode.NotFound;
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient();
        if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(mode == "missing" ? source.Id + 1000 : source.Id, legacy), request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync()).Title);
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteHttp_ClearFlagCannotUseIgnoredValueToAcceptAConflict(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.ClearCourtClaimAmount = true; request.CourtClaimAmount = 999m;
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtClaimAmount, 999m));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(999m, (await db.ClientCases.AsNoTracking().SingleAsync()).CourtClaimAmount);
    }

    private static readonly PropertyInfo[] CaseScalarProperties = typeof(ClientCase).GetProperties()
        .Where(property => property.SetMethod is not null && (property.PropertyType.IsValueType || property.PropertyType == typeof(string))).ToArray();

    private static CaseEditRequest EditRequest(ClientCase source)
    {
        var original = new ClientCase(); var request = new CaseEditRequest { ExpectedSource = original };
        foreach (var property in CaseScalarProperties) { property.SetValue(original, property.GetValue(source)); property.SetValue(request, property.GetValue(source)); }
        return request;
    }

    private static string CaseWriteRoute(int id, bool legacy, bool oldRoute = false)
        => $"/api/{(legacy ? "cases" : "v2/cases")}/{id}{(oldRoute ? string.Empty : "/checked")}";

    private static TestServer CaseWriteServer(PaymentContext db, User user) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddAuthentication("write-fixture").AddScheme<AuthenticationSchemeOptions, ConfirmationAuthentication>("write-fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly).AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            });
            services.AddSingleton<IControllerActivator>(new CaseWriteControllerActivator(db, user));
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class CaseWriteControllerActivator(PaymentContext db, User user) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            ControllerBase controller = context.ActionDescriptor.ControllerTypeInfo.AsType() switch
            {
                var type when type == typeof(CasesController) => new CasesController(db),
                var type when type == typeof(PayPlanner.Api.Controllers.V1.CasesV1Controller) => new PayPlanner.Api.Controllers.V1.CasesV1Controller(db),
                var type when type == typeof(PayPlanner.Api.Controllers.ClientsController) => ClientsControllerTests.AtomicCreateController(db, user.Id),
                var type when type == typeof(PayPlanner.Api.Controllers.V1.ClientsV1Controller) => new PayPlanner.Api.Controllers.V1.ClientsV1Controller(db),
                var type when type == typeof(ClientsV2Controller) => new ClientsV2Controller(db),
                var type when type == typeof(CashOrdersController) => new CashOrdersController(db),
                _ => CreateController(db, user),
            };
            controller.ControllerContext = context; return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }
}
