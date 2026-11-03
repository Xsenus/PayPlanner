using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static ClientsController RequiredClientController(PaymentContext db, User actor) => new(db, null!, new FormRequirementsService(db))
    { ControllerContext = ClientsControllerTests.AtomicCreateController(db, actor.Id).ControllerContext };

    private static Task<IActionResult> WriteRequiredClientAsync(PaymentContext db, User actor, int id, Client model, int route, bool create)
    {
        var root = RequiredClientController(db, actor);
        if (route == 0) return create ? root.Create(model, default) : root.Update(id, model, default);
        if (route == 1) { var v1 = new ClientsV1Controller(db) { ControllerContext = root.ControllerContext }; return create ? v1.Create(model, default) : v1.Update(id, model, default); }
        var v2 = new ClientsV2Controller(db) { ControllerContext = root.ControllerContext }; return create ? v2.Create(model, default) : v2.Update(id, model, default);
    }

    private sealed class RequiredClientActivator(PaymentContext db, User actor) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            var type = context.ActionDescriptor.ControllerTypeInfo.AsType();
            ControllerBase controller = type == typeof(ClientsController) ? RequiredClientController(db, actor)
                : type == typeof(ClientsV1Controller) ? new ClientsV1Controller(db)
                : type == typeof(ClientsV2Controller) ? new ClientsV2Controller(db)
                : throw new InvalidOperationException("Unexpected required-fields endpoint.");
            controller.ControllerContext = context; return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }

    private static TestServer RequiredClientServer(PaymentContext db, User actor, Func<Task>? before = null) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging(); services.AddAuthentication("required-fixture").AddScheme<AuthenticationSchemeOptions, ConfirmationAuthentication>("required-fixture", _ => { });
            services.AddAuthorization(); services.AddControllers().AddApplicationPart(typeof(ClientsController).Assembly).AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            });
            services.AddSingleton<IControllerActivator>(new RequiredClientActivator(db, actor));
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            if (before is not null) app.Use(async (_, next) => { await before(); await next(); });
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    private static async Task<HttpResponseMessage> SendRequiredClientAsync(HttpClient http, int route, bool create, int id, Client input)
    {
        var path = route == 0 ? "/api/clients" : $"/api/v{route}/clients";
        return create ? await http.PostAsJsonAsync(path, input) : await http.PutAsJsonAsync($"{path}/{id}", input);
    }

    private static async Task ConfigureRequiredClientAsync(PaymentContext db, bool create, params string[] fields)
    {
        foreach (var field in fields) db.FormFieldRequirements.Add(new FormFieldRequirement { Section = create ? "clients-modal" : "clients", FieldKey = field, IsRequired = true });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    }

    private static Client RequiredClientInput(User actor, string mode)
    {
        var profile = JsonNode.Parse(BirthdayNotes("1990-01-02")[BirthdayProfilePrefix.Length..])!.AsObject();
        profile["firstName"] = "Синтетическое имя";
        if (mode is "scalar" or "stored" or "missing") profile.Remove("birthDate");
        if (mode == "clear") profile["birthDate"] = null;
        if (mode == "blank") profile["birthDate"] = "  ";
        var input = new Client { Name = "Проверка обязательных полей", Phone = "+79990001122", Email = "required@example.test", ResponsibleUserId = actor.Id, Notes = BirthdayProfilePrefix + profile.ToJsonString() };
        if (mode is "scalar" or "equal") input.BirthDate = new DateTime(1990, 1, 2, 17, 45, 0, DateTimeKind.Utc);
        return input;
    }

    private static async Task AssertRequiredClientSavedAsync(PaymentContext db, HttpResponseMessage response, Client input, bool create, DateTime? expected, int originalId, int[] cases)
    {
        Assert.True(response.StatusCode == (create ? HttpStatusCode.Created : HttpStatusCode.OK), $"HTTP={(int)response.StatusCode};body={await response.Content.ReadAsStringAsync()}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetInt32(); var saved = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(expected, saved.BirthDate); Assert.Equal(input.Name, saved.Name); Assert.Equal(input.Notes, saved.Notes);
        Assert.Equal(create ? 2 : 1, await db.Clients.CountAsync());
        if (create) Assert.Equal(OldClientBirthday, await db.Clients.Where(x => x.Id == originalId).Select(x => x.BirthDate).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    public static IEnumerable<object[]> RequiredClientScalarCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from postgres in new[] { false, true } select new object[] { route, create, postgres };

    [Theory]
    [MemberData(nameof(RequiredClientScalarCases))]
    public async Task ClientRequiredBoundary_AcceptsScalarBirthdayWithoutProfileCopy(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "BirthDate"); var input = RequiredClientInput(actor, "scalar");
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, create, id, input);
        await AssertRequiredClientSavedAsync(db, response, input, create, new DateTime(1990, 1, 2), id, cases);
    }

    public static IEnumerable<object[]> RequiredClientProfileCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from mode in new[] { "profile", "equal" } from postgres in new[] { false, true } select new object[] { route, create, mode, postgres };

    [Theory]
    [MemberData(nameof(RequiredClientProfileCases))]
    public async Task ClientRequiredBoundary_AcceptsProfileAndConsistentScalarDate(int route, bool create, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "BirthDate"); var input = RequiredClientInput(actor, mode);
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, create, id, input);
        await AssertRequiredClientSavedAsync(db, response, input, create, new DateTime(1990, 1, 2), id, cases);
    }

    public static IEnumerable<object[]> RequiredClientStoredCases() => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } select new object[] { route, postgres };

    [Theory]
    [MemberData(nameof(RequiredClientStoredCases))]
    public async Task ClientRequiredBoundary_RetainsStoredBirthdayWhenRequestOmitsIt(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, false, "BirthDate"); var input = RequiredClientInput(actor, "stored");
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, false, id, input);
        await AssertRequiredClientSavedAsync(db, response, input, false, OldClientBirthday, id, cases);
    }

    public static IEnumerable<object[]> RequiredClientMissingCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from mode in new[] { "missing", "clear", "blank" } from postgres in new[] { false, true } select new object[] { route, create, mode, postgres };

    [Theory]
    [MemberData(nameof(RequiredClientMissingCases))]
    public async Task ClientRequiredBoundary_RejectsMissingOrClearedRequiredBirthday(int route, bool create, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "BirthDate");
        if (!create && mode == "missing") await db.Clients.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.BirthDate, (DateTime?)null).SetProperty(x => x.Notes, BirthdayNotes(null)));
        var input = RequiredClientInput(actor, mode); var before = await ClientContactSnapshotAsync(db);
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, create, id, input);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"route={route};create={create};mode={mode};HTTP={(int)response.StatusCode};clients={await db.Clients.CountAsync()}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Contains("BirthDate", json.RootElement.GetProperty("missingFields").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(before, await ClientContactSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Theory]
    [MemberData(nameof(RequiredClientScalarCases))]
    public async Task ClientRequiredBoundary_KeepsExistingEmailAndUpdatePhoneExceptions(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, create ? ["Email"] : ["Email", "Phone"]); var input = RequiredClientInput(actor, "profile"); input.Email = ""; if (!create) input.Phone = "";
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, id, input);
        await AssertRequiredClientSavedAsync(db, response, input, create, new DateTime(1990, 1, 2), id, cases);
    }

    [Theory]
    [MemberData(nameof(RequiredClientScalarCases))]
    public async Task ClientRequiredBoundary_RejectsNullableRequiredFullNameWithoutAnyWrite(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "FullName"); var input = RequiredClientInput(actor, "profile"); var profile = JsonNode.Parse(input.Notes![BirthdayProfilePrefix.Length..])!.AsObject(); profile["fullName"] = null; input.Notes = BirthdayProfilePrefix + profile.ToJsonString(); var before = await ClientContactSnapshotAsync(db);
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, id, input);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"route={route};create={create};HTTP={(int)response.StatusCode}"); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Contains("FullName", json.RootElement.GetProperty("missingFields").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(before, await ClientContactSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Theory]
    [MemberData(nameof(RequiredClientStoredCases))]
    public async Task ClientRequiredBoundary_KeepsLegalQuickClientFieldSubset(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db);
        foreach (var field in new[] { "BirthDate", "FullName", "Inn", "ShortName" }) db.FormFieldRequirements.Add(new FormFieldRequirement { Section = "clients-modal-legal", FieldKey = field, IsRequired = true }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var input = RequiredClientInput(actor, "clear"); var profile = JsonNode.Parse(input.Notes![BirthdayProfilePrefix.Length..])!.AsObject(); profile["kind"] = "legal"; profile["shortName"] = "Синтетическая организация"; profile["inn"] = "7700000000"; profile["fullName"] = null; input.Notes = BirthdayProfilePrefix + profile.ToJsonString();
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, true, id, input);
        await AssertRequiredClientSavedAsync(db, response, input, true, null, id, cases);
    }

    public static IEnumerable<object[]> RequiredClientFirstNameCases() => from route in new[] { 0, 1, 2 } from present in new[] { false, true } from postgres in new[] { false, true } select new object[] { route, present, postgres };

    [Theory]
    [MemberData(nameof(RequiredClientFirstNameCases))]
    public async Task ClientRequiredBoundary_KeepsConfiguredLegacyNameAlias(int route, bool present, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, true, "Name"); var input = RequiredClientInput(actor, "profile");
        if (!present) { var profile = JsonNode.Parse(input.Notes![BirthdayProfilePrefix.Length..])!.AsObject(); profile.Remove("firstName"); input.Notes = BirthdayProfilePrefix + profile.ToJsonString(); }
        var before = await ClientContactSnapshotAsync(db);
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, true, id, input);
        if (present) await AssertRequiredClientSavedAsync(db, response, input, true, new DateTime(1990, 1, 2), id, cases);
        else { Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"route={route};present={present};HTTP={(int)response.StatusCode}"); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Contains("FirstName", json.RootElement.GetProperty("missingFields").EnumerateArray().Select(x => x.GetString())); Assert.Equal(before, await ClientContactSnapshotAsync(db)); }
    }

    [Theory]
    [MemberData(nameof(RequiredClientScalarCases))]
    public async Task ClientRequiredBoundary_RejectsDateConflictBeforeSavingRequiredProfile(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "BirthDate"); var input = RequiredClientInput(actor, "equal"); input.BirthDate = new DateTime(2001, 2, 3); var before = await ClientContactSnapshotAsync(db);
        using var server = RequiredClientServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, id, input);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await ClientContactSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    public static IEnumerable<object[]> RequiredClientAdmissionCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from approval in new[] { false, true } select new object[] { route, create, approval };

    [Theory]
    [MemberData(nameof(RequiredClientAdmissionCases))]
    public async Task ClientRequiredBoundary_RechecksApprovalAndActionBeforeRequiredFieldErrors(int route, bool create, bool approval)
    {
        await using var db = await CreateContextAsync(); var (actor, cases, id) = await SeedBirthdayClientAsync(db); await ConfigureRequiredClientAsync(db, create, "BirthDate"); var input = RequiredClientInput(actor, "clear"); var before = await ClientContactSnapshotAsync(db);
        using var server = RequiredClientServer(db, actor, async () =>
        {
            if (approval) await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
            else if (create) await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false));
            else await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        });
        using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, id, input);
        Assert.Equal(approval ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(before, await ClientContactSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    private sealed class RequiredClientAfterRead(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (!called && command.CommandText.Contains("\"FormFieldRequirements\"", StringComparison.Ordinal)) { called = true; await action(ct); } return result; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClientRequiredBoundary_PgPinsRetainedDateWhileReadingRequirements(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var (actor, cases, id) = await SeedBirthdayClientAsync(setup); await ConfigureRequiredClientAsync(setup, false, "BirthDate"); var called = false; var blocked = false; var committed = false;
        var hook = new RequiredClientAfterRead(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try { Assert.Equal(1, await concurrent.Clients.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.BirthDate, new DateTime(2001, 2, 3)), ct)); await tx.CommitAsync(ct); committed = true; }
            catch (Exception error) { Exception? cause = error; while (cause is not null && cause is not PostgresException) cause = cause.InnerException; if (cause is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) throw; blocked = true; }
        });
        await using var request = Open(hook); Assert.IsType<OkObjectResult>(await WriteRequiredClientAsync(request, actor, id, RequiredClientInput(actor, "stored"), route, false)); Assert.True(called && blocked && !committed); Assert.Equal(OldClientBirthday, await setup.Clients.Where(x => x.Id == id).Select(x => x.BirthDate).SingleAsync()); await AssertBulkCasesUnchangedAsync(setup, cases);
    }
}
