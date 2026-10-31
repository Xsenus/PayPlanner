using System.Net;
using System.Collections;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateGraphTests
{
    [Fact]
    public async Task EveryMappedRelationshipIsRejectedWithoutWalkingItsChildren()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var entity = db.Model.FindEntityType(typeof(Payment))!;
        var navigations = entity.GetNavigations().Cast<INavigationBase>().Concat(entity.GetSkipNavigations()).ToArray();
        Assert.NotEmpty(navigations);
        foreach (var navigation in navigations)
        {
            var model = PaymentFor(fixture); var property = navigation.PropertyInfo!; Assert.NotNull(property);
            var child = Activator.CreateInstance(navigation.TargetEntityType.ClrType)!;
            if (navigation.IsCollection)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(navigation.TargetEntityType.ClrType))!;
                list.Add(child); property.SetValue(model, list);
            }
            else property.SetValue(model, child);
            var before = await SnapshotAsync(db);
            var rejected = Assert.IsType<BadRequestObjectResult>(await CreateAsync(db, fixture, 0, model));
            Assert.Contains("Связанные записи", System.Text.Json.JsonSerializer.SerializeToElement(rejected.Value).GetProperty("message").GetString());
            Assert.Equal(before, await SnapshotAsync(db));
            Assert.False(db.ChangeTracker.HasChanges());
        }
    }

    [Fact]
    public async Task ExplicitNullCollectionsAreRejectedAndEmptyCollectionsRemainAllowed()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, 0, PaymentFor(fixture)));
        var entity = db.Model.FindEntityType(typeof(Payment))!;
        foreach (var navigation in entity.GetNavigations().Cast<INavigationBase>().Concat(entity.GetSkipNavigations()).Where(n => n.IsCollection))
        {
            var model = PaymentFor(fixture); navigation.PropertyInfo!.SetValue(model, null); var before = await SnapshotAsync(db);
            var rejected = Assert.IsType<BadRequestObjectResult>(await CreateAsync(db, fixture, 0, model));
            Assert.Contains("Связанные записи", System.Text.Json.JsonSerializer.SerializeToElement(rejected.Value).GetProperty("message").GetString());
            Assert.Equal(before, await SnapshotAsync(db));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ScalarReferencesCreateOnlyThePayment(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        var category = new IncomeType { Name = "Existing category", PaymentType = PaymentType.Income }; db.IncomeTypes.Add(category); await db.SaveChangesAsync();
        var model = PaymentFor(fixture); model.IncomeTypeId = category.Id;
        var clients = await db.Clients.CountAsync(); var users = await db.Users.CountAsync();
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(db, fixture, api, model)).Value);
        Assert.True(saved.Id > 0); Assert.NotEqual(fixture.PaymentId, saved.Id); Assert.Equal(category.Id, saved.IncomeTypeId);
        Assert.Equal(legal ? fixture.OwnerId : (int?)null, saved.LegalEntityId);
        Assert.Equal(legal ? (int?)null : fixture.OwnerId, saved.ClientId);
        Assert.Equal(2, await db.Payments.CountAsync()); Assert.Equal(clients, await db.Clients.CountAsync()); Assert.Equal(users, await db.Users.CountAsync());
        Assert.Single(await db.IncomeTypes.ToArrayAsync());
    }

    public static IEnumerable<object[]> DeniedCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var denial in new[] { "permission", "inactive", "unapproved", "missing" }) yield return [api, denial];
    }
    [Theory]
    [MemberData(nameof(DeniedCases))]
    public async Task CurrentAccessIsCheckedBeforeTheUntrustedGraph(int api, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var user = await db.Users.SingleAsync();
        if (denial == "permission") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanCreate = false;
        if (denial == "inactive") user.IsActive = false;
        if (denial == "unapproved") user.IsApproved = false;
        await db.SaveChangesAsync(); if (denial == "missing") fixture = fixture with { ActorId = int.MaxValue };
        var model = PaymentFor(fixture); AddGraph(model, "client"); var before = await SnapshotAsync(db);
        var response = await CreateAsync(db, fixture, api, model);
        if (denial == "permission") Assert.IsType<ForbidResult>(response); else Assert.IsType<UnauthorizedResult>(response);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData("client")]
    [InlineData("id")]
    public async Task DedicatedAttemptProtocolStillRejectsUnknownGraphFields(string field)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var model = await PaymentCreateFinancialBoundaryTests.NewPaymentAsync(db, seed.Receipt);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, seed.Receipt); var attempt = await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller);
        var command = System.Text.Json.Nodes.JsonNode.Parse(PaymentCreateFinancialBoundaryTests.Command(model).GetRawText())!.AsObject();
        command[field] = field == "id" ? System.Text.Json.Nodes.JsonValue.Create(199999) : System.Text.Json.JsonSerializer.SerializeToNode(new { name = "Injected" });
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await controller.CommitCreateAttempt(attempt, System.Text.Json.JsonSerializer.SerializeToElement(command), default));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.SingleAsync()).State);
    }

    public static IEnumerable<object[]> GraphCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var graph in new[] { "client", "category", "user" }) yield return [api, graph];
    }

    [Theory]
    [MemberData(nameof(GraphCases))]
    public async Task CreateCannotInsertRelatedRecordsThroughAnEntityGraph(int api, string graph)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "clients")).CanCreate = false; await db.SaveChangesAsync();
        var model = PaymentFor(fixture); AddGraph(model, graph); var before = await SnapshotAsync(db);
        var response = await CreateAsync(db, fixture, api, model);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.IsType<BadRequestObjectResult>(response);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CallerCannotChooseTheIdentityOfANewPayment(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = PaymentFor(fixture); model.Id = 199999; var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateAsync(db, fixture, api, model));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WireCreateCannotBypassTheDeniedClientCreationPermission(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "clients")).CanCreate = false; await db.SaveChangesAsync();
        using var server = Server(db, fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var model = PaymentFor(fixture); AddGraph(model, "client"); var before = await SnapshotAsync(db);
        var route = api == 0 ? "/api/payments" : $"/api/v{api}/payments";
        using var response = await client.PostAsJsonAsync(route, model);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    internal static Task<IActionResult> CreateAsync(PaymentContext db, Fixture fixture, int api, Payment model)
    {
        var context = BalanceApplicationPaymentWriterTests.Controller(db, fixture).ControllerContext;
        return api == 0 ? BalanceApplicationPaymentWriterTests.Controller(db, fixture).Create(model, default)
            : api == 1 ? new PaymentsV1Controller(db) { ControllerContext = context }.Create(model)
            : new PaymentsV2Controller(db) { ControllerContext = context }.Create(model);
    }

    internal static void AddGraph(Payment model, string graph)
    {
        if (graph == "client") model.Client = new Client { Name = "Injected client despite denied creation" };
        if (graph == "category") model.IncomeType = new IncomeType { Name = "Injected private category", PaymentType = PaymentType.Income };
        if (graph == "user") model.Responsibles.Add(new PaymentResponsible
        {
            User = new User
            {
                Email = "injected@graph-test.local",
                FullName = "Injected user",
                PasswordHash = "unused-test",
                IsActive = true,
                IsApproved = true,
                IsEmployee = true,
                Role = new Role { Name = "injected-role", Permissions = [new RolePermission { Section = "payments", CanView = true, CanCreate = true, CanEdit = true, CanDelete = true, ViewScope = "all" }] }
            }
        });
    }

    internal static async Task<string> SnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.ClientId, p.LegalEntityId, p.IncomeTypeId, p.Amount, p.PaidAmount }).ToArrayAsync(),
            clients = await db.Clients.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToArrayAsync(),
            users = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => new { u.Id, u.Email, u.RoleId }).ToArrayAsync(),
            roles = await db.Roles.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, r.Name }).ToArrayAsync(),
            categories = await db.IncomeTypes.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, r.Name }).ToArrayAsync(),
            responsibleCount = await db.PaymentResponsibles.CountAsync(),
            auditCount = await db.ObjectAuditEvents.CountAsync(),
            balanceCount = await db.ClientBalanceTransactions.CountAsync()
        });

    private static TestServer Server(PaymentContext db, Fixture fixture) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging(); services.AddSingleton(db);
            services.AddAuthentication("graph-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("graph-fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(PaymentsController).Assembly)
                .AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles; })
                .AddControllersAsServices();
            services.AddTransient<PaymentsController>(_ => BalanceApplicationPaymentWriterTests.Controller(db, fixture));
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

public sealed class PaymentCreateGraphPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [MemberData(nameof(PaymentCreateGraphTests.GraphCases), MemberType = typeof(PaymentCreateGraphTests))]
    public async Task NativeCreateRejectsTheRelatedGraphWithoutAnyAuditOrLinkedInsert(int api, string graph)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var model = PaymentFor(fixture); PaymentCreateGraphTests.AddGraph(model, graph);
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeCreateCannotChooseTheNewPrimaryKey(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var model = PaymentFor(fixture); model.Id = 199999; var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task NativeScalarReferencesStillCreateOnlyTheAuthorizedPayment(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legal); var model = PaymentFor(fixture);
        var clients = await db.Clients.CountAsync(); var users = await db.Users.CountAsync(); var roles = await db.Roles.CountAsync();
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await PaymentCreateGraphTests.CreateAsync(db, fixture, api, model)).Value);
        Assert.Equal(legal ? fixture.OwnerId : (int?)null, saved.LegalEntityId); Assert.Equal(legal ? (int?)null : fixture.OwnerId, saved.ClientId);
        Assert.Equal(2, await db.Payments.CountAsync()); Assert.Equal(clients, await db.Clients.CountAsync()); Assert.Equal(users, await db.Users.CountAsync()); Assert.Equal(roles, await db.Roles.CountAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection)
        => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
}
