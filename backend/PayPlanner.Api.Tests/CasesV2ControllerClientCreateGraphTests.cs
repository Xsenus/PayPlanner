using System.Collections;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_HttpCannotCreatePaymentWithoutPaymentPermissionOrKey(bool v2)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients);
        var input = new Client { Name = "Новый клиент", IsActive = true, Payments = [new Payment { Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", input);
        var payments = await db.Payments.AsNoTracking().CountAsync(); var clients = await db.Clients.AsNoTracking().CountAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest && payments == 0 && clients == 1, $"HTTP {(int)response.StatusCode}; payments={payments}; clients={clients}");
        Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientCreateGraph_PgRejectsNestedPaymentWithAndWithoutPaymentAccess(bool v2, bool financialAccess)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients); if (financialAccess) await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", new Client { Name = "Вложенный платёж", Payments = [new Payment { Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync()); Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_HttpRejectsPaymentInsideANestedCase(bool v2)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients);
        var existingOwner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var input = new Client { Name = "Вложенное дело", Cases = [new ClientCase { Title = "Незапрошенное дело", Payments = [new Payment { ClientId = existingOwner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] }] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", input); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_AllIndependentRootRelationsAreRejected(bool v2)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients);
        var entity = db.Model.FindEntityType(typeof(Client))!;
        var navigations = entity.GetNavigations().Cast<INavigationBase>().Concat(entity.GetSkipNavigations()).Where(row => row.Name is not (nameof(Client.Addresses) or nameof(Client.Passports))).ToArray(); Assert.Equal(9, navigations.Length); Assert.Contains(navigations, row => row.Name == nameof(Client.Contracts));
        foreach (var navigation in navigations)
        {
            var input = new Client { Name = "Запрещённая связь " + navigation.Name }; var related = Activator.CreateInstance(navigation.TargetEntityType.ClrType)!;
            if (navigation.IsCollection) { var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(navigation.TargetEntityType.ClrType))!; list.Add(related); navigation.PropertyInfo!.SetValue(input, list); }
            else navigation.PropertyInfo!.SetValue(input, related);
            Assert.IsType<BadRequestObjectResult>(await CreateGraphClientAsync(db, user, input, v2)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
        }
        Assert.Equal(1, await db.Clients.CountAsync()); Assert.Equal(1, await db.Users.CountAsync()); Assert.Empty(await db.LegalEntities.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "address-client")]
    [InlineData(true, "address-client")]
    [InlineData(false, "address-legal")]
    [InlineData(true, "address-legal")]
    [InlineData(false, "address-legal-id")]
    [InlineData(true, "address-legal-id")]
    [InlineData(false, "passport-client")]
    [InlineData(true, "passport-client")]
    public async Task ClientCreateGraph_OwnedProfileCannotLeadToAnotherOwner(bool v2, string target)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients);
        var input = new Client { Name = "Профиль с чужим владельцем" };
        if (target == "passport-client") input.Passports = [new ClientPassport { Series = "AA", Number = "000111", Client = new Client { Name = "Вложенный владелец", Payments = [new Payment { Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] } }];
        else input.Addresses = [new AddressRecord { Value = "Синтетический адрес", Client = target == "address-client" ? new Client { Name = "Вложенный владелец" } : null, LegalEntity = target == "address-legal" ? new LegalEntity { ShortName = "Вложенная организация" } : null, LegalEntityId = target == "address-legal-id" ? 999 : null }];
        Assert.IsType<BadRequestObjectResult>(await CreateGraphClientAsync(db, user, input, v2)); Assert.False(db.ChangeTracker.HasChanges()); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync()); Assert.Empty(await db.AddressRecords.ToArrayAsync()); Assert.Empty(await db.ClientPassports.ToArrayAsync()); Assert.Empty(await db.LegalEntities.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_KeepsOwnedAddressPassportNotesAndServerAssignedResponsible(bool v2)
    {
        await using var db = await CreateContextAsync(); var (user, _) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients); var other = await CreateUserAsync(db, "client-create-owner@example.local", "Другой");
        const string notes = "__PP_CLIENT_PROFILE_V2__:{\"firstName\":\"Синтетический\",\"passportSeries\":\"AA\"}";
        var input = new Client { Name = "Составной профиль", Notes = notes, Address = "Синтетический адрес", Phone = "+70000000001", Email = "profile@example.local", ResponsibleUserId = other.Id, BirthDate = new DateTime(1990, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        input.Addresses.Add(new AddressRecord { Client = input, Type = AddressType.Registration, Value = input.Address, UnrestrictedValue = "Полный синтетический адрес", PostalCode = "000001", FiasId = "synthetic-fias", IsPrimary = true });
        input.Passports.Add(new ClientPassport { Client = input, Series = "AA", Number = "000111", IssuedBy = "Синтетическое подразделение", DepartmentCode = "000-001", IsPrimary = true });
        var result = Assert.IsType<CreatedResult>(await CreateGraphClientAsync(db, user, input, v2)); var saved = Assert.IsType<Client>(result.Value); Assert.True(saved.Id > 0); Assert.Equal(user.Id, saved.ResponsibleUserId); Assert.Equal(notes, saved.Notes); Assert.Equal(input.BirthDate, saved.BirthDate); Assert.EndsWith($"/{saved.Id}", result.Location);
        var address = Assert.Single(await db.AddressRecords.AsNoTracking().ToArrayAsync()); Assert.Equal(saved.Id, address.ClientId); Assert.Null(address.LegalEntityId); Assert.Equal("synthetic-fias", address.FiasId); Assert.Equal("Полный синтетический адрес", address.UnrestrictedValue); Assert.True(address.IsPrimary);
        var passport = Assert.Single(await db.ClientPassports.AsNoTracking().ToArrayAsync()); Assert.Equal(saved.Id, passport.ClientId); Assert.Equal("AA", passport.Series); Assert.Equal("000111", passport.Number); Assert.Equal("000-001", passport.DepartmentCode); Assert.True(passport.IsPrimary);
        Assert.Equal(2, await db.Clients.CountAsync()); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(2, await db.ClientCases.CountAsync()); Assert.Equal(3, await db.TaskJournalItems.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_HttpKeepsOrdinaryProfileNotesAndAddress(bool v2)
    {
        await using var db = await CreateContextAsync(); var (user, _) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients);
        const string notes = "__PP_CLIENT_PROFILE_V2__:{\"firstName\":\"Синтетический\",\"registrationAddress\":\"Синтетический адрес\"}";
        var input = new Client { Name = "Обычный профиль", Notes = notes, Address = "Синтетический адрес", Addresses = [new AddressRecord { Type = AddressType.Registration, Value = "Синтетический адрес", PostalCode = "000001", IsPrimary = true }] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", input); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); var id = json.GetProperty("id").GetInt32(); Assert.True(id > 0); Assert.Equal(notes, json.GetProperty("notes").GetString()); Assert.Equal(user.Id, json.GetProperty("responsibleUserId").GetInt32()); Assert.EndsWith($"/{id}", response.Headers.Location!.ToString());
        Assert.Equal(notes, await db.Clients.Where(row => row.Id == id).Select(row => row.Notes).SingleAsync()); Assert.Equal(id, (await db.AddressRecords.SingleAsync()).ClientId); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(2, await db.ClientCases.CountAsync()); Assert.Equal(3, await db.TaskJournalItems.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_PgRejectsPaymentInsideManyToManyContract(bool v2)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients); var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var input = new Client { Name = "Вложенный договор", Contracts = [new Contract { Number = "synthetic-contract", Title = "Незапрошенный договор", Amount = 99.95m, Payments = [new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] }] };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", input);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync()); Assert.Empty(await db.Contracts.ToArrayAsync()); Assert.Empty(await db.ClientContracts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCreateGraph_PgKeepsOwnedProfileWhenIndependentRelationsAreEmpty(bool v2)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (user, _) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients); var input = new Client { Name = "Профиль PostgreSQL", Notes = "Исходные заметки" };
        input.Addresses.Add(new AddressRecord { Client = input, Type = AddressType.Registration, Value = "Синтетический адрес", IsPrimary = true }); input.Passports.Add(new ClientPassport { Client = input, Series = "AA", Number = "000111", IsPrimary = true });
        var result = Assert.IsType<CreatedResult>(await CreateGraphClientAsync(db, user, input, v2)); var saved = Assert.IsType<Client>(result.Value); Assert.True(saved.Id > 0); Assert.Equal(user.Id, saved.ResponsibleUserId); Assert.Equal(input.Notes, saved.Notes);
        Assert.Equal(saved.Id, (await db.AddressRecords.SingleAsync()).ClientId); Assert.Equal(saved.Id, (await db.ClientPassports.SingleAsync()).ClientId); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Empty(await db.Contracts.ToArrayAsync()); Assert.Equal(2, await db.ClientCases.CountAsync()); Assert.Equal(3, await db.TaskJournalItems.CountAsync());
    }

    private static Task<IActionResult> CreateGraphClientAsync(PaymentContext db, User user, Client input, bool v2)
    {
        var context = CreateController(db, user).ControllerContext;
        return v2 ? new PayPlanner.Api.Controllers.V2.ClientsV2Controller(db) { ControllerContext = context }.Create(input, CancellationToken.None)
            : new PayPlanner.Api.Controllers.V1.ClientsV1Controller(db) { ControllerContext = context }.Create(input, CancellationToken.None);
    }
}
