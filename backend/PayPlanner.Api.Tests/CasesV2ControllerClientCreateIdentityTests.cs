using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> ClientCreationIdentityCases()
        => from v2 in new[] { false, true } from field in new[] { "client-id", "address-id", "address-client-id", "passport-id", "passport-client-id" } from negative in new[] { false, true } select new object[] { v2, field, negative };

    [Theory]
    [MemberData(nameof(ClientCreationIdentityCases))]
    public async Task ClientCreateIdentity_HttpRejectsExplicitIdentitiesAndForeignOwnerIds(bool v2, string field, bool negative)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var existing = await db.Clients.Select(row => row.Id).SingleAsync();
        var input = new Client { Name = "Профиль с переданным ID" }; var address = new AddressRecord { Value = "Синтетический адрес", Type = AddressType.Registration }; var passport = new ClientPassport { Series = "AA", Number = "000111", IsPrimary = true };
        var supplied = negative ? -987654 : field.EndsWith("client-id", StringComparison.Ordinal) && field != "client-id" ? existing : 987654;
        if (field == "client-id") input.Id = supplied;
        if (field.StartsWith("address", StringComparison.Ordinal)) { if (field == "address-id") address.Id = supplied; else address.ClientId = supplied; input.Addresses.Add(address); }
        if (field.StartsWith("passport", StringComparison.Ordinal)) { if (field == "passport-id") passport.Id = supplied; else passport.ClientId = supplied; input.Passports.Add(passport); }
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PostAsJsonAsync($"/api/v{(v2 ? 2 : 1)}/clients", input);
        var clients = await db.Clients.CountAsync(); var addresses = await db.AddressRecords.CountAsync(); var passports = await db.ClientPassports.CountAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest && clients == 1 && addresses == 0 && passports == 0, $"HTTP {(int)response.StatusCode}; field={field}; supplied={supplied}; clients={clients}; addresses={addresses}; passports={passports}"); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, "address-id")]
    [InlineData(false, "passport-id")]
    [InlineData(true, "address-id")]
    [InlineData(true, "passport-id")]
    public async Task ClientCreateIdentity_RejectsAnExistingChildBeforeEfCanAttachIt(bool v2, string target)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var owner = await db.Clients.Select(row => row.Id).SingleAsync();
        var sourceAddress = new AddressRecord { ClientId = owner, Value = "Прежний адрес" }; var sourcePassport = new ClientPassport { ClientId = owner, Series = "Прежняя", Number = "000111" }; db.AddressRecords.Add(sourceAddress); db.ClientPassports.Add(sourcePassport); await db.SaveChangesAsync();
        var input = new Client { Name = "Не присоединять прежние строки" }; if (target == "address-id") input.Addresses.Add(new AddressRecord { Id = sourceAddress.Id, Value = "Подменённый адрес" }); else input.Passports.Add(new ClientPassport { Id = sourcePassport.Id, Series = "Подменённая", Number = "000222" });
        Assert.IsType<BadRequestObjectResult>(await CreateGraphClientAsync(db, actor, input, v2)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(owner, (await db.AddressRecords.AsNoTracking().SingleAsync()).ClientId); Assert.Equal("Прежний адрес", (await db.AddressRecords.AsNoTracking().SingleAsync()).Value); Assert.Equal(owner, (await db.ClientPassports.AsNoTracking().SingleAsync()).ClientId); Assert.Equal("Прежняя", (await db.ClientPassports.AsNoTracking().SingleAsync()).Series); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientCreateIdentity_PgRejectsSuppliedIdsWithoutChangingOldProfiles(bool v2, bool parent)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var input = new Client { Id = parent ? 987654 : 0, Name = "Сервер назначает ID PostgreSQL" }; if (!parent) input.Addresses.Add(new AddressRecord { Id = 987654, Value = "Адрес" });
        var result = await CreateGraphClientAsync(db, actor, input, v2); var count = await db.Clients.CountAsync(); Assert.True(result is BadRequestObjectResult && count == 1, $"result={result.GetType().Name}; parent={parent}; clients={count}"); Assert.Empty(await db.AddressRecords.ToArrayAsync()); Assert.Empty(await db.ClientPassports.ToArrayAsync()); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientCreateIdentity_NewProfileUsesServerStampsAndKeepsBusinessValues(bool v2, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var spoof = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc); var birthDate = new DateTime(1990, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var input = new Client { Name = "Обычный составной профиль", Notes = "Исходные заметки", BirthDate = birthDate, CreatedAt = spoof, Phone = "+70000000001", Email = "profile@example.local" };
        input.Addresses.Add(new AddressRecord { Client = input, ClientId = 0, Value = "Синтетический адрес", Country = "Россия", FiasId = "synthetic-fias", CreatedAt = spoof, UpdatedAt = spoof, IsPrimary = true });
        input.Passports.Add(new ClientPassport { Client = input, ClientId = 0, Series = "AA", Number = "000111", IssuedBy = "Синтетический отдел", IssuedAt = spoof, ValidFrom = spoof, CreatedAt = spoof, UpdatedAt = spoof, IsPrimary = true });
        var before = DateTime.UtcNow; var created = Assert.IsType<CreatedResult>(await CreateGraphClientAsync(db, actor, input, v2)); var returned = Assert.IsType<Client>(created.Value); var after = DateTime.UtcNow; var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == returned.Id); var address = await db.AddressRecords.AsNoTracking().SingleAsync(); var passport = await db.ClientPassports.AsNoTracking().SingleAsync();
        var forged = saved.CreatedAt < before || saved.CreatedAt > after || address.CreatedAt < before || address.CreatedAt > after || passport.CreatedAt < before || passport.CreatedAt > after || address.UpdatedAt == spoof || passport.UpdatedAt == spoof;
        Assert.True(!forged, $"forgedStamps={forged}; client={saved.CreatedAt:O}; address={address.CreatedAt:O}; passport={passport.CreatedAt:O}"); Assert.True(saved.Id > 0 && address.Id > 0 && passport.Id > 0); Assert.Equal(saved.Id, address.ClientId); Assert.Equal(saved.Id, passport.ClientId); Assert.Equal(actor.Id, saved.ResponsibleUserId); Assert.Equal(input.Name, saved.Name); Assert.Equal(input.Notes, saved.Notes); Assert.Equal(birthDate, saved.BirthDate); Assert.Equal("Россия", address.Country); Assert.Equal("synthetic-fias", address.FiasId); Assert.Equal("AA", passport.Series); Assert.Equal("000111", passport.Number); Assert.Equal(spoof, passport.IssuedAt); Assert.Equal(spoof, passport.ValidFrom);
        Assert.Equal(saved.CreatedAt, returned.CreatedAt); Assert.Equal(3, await db.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Status == "done")).Result); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.All(await db.ClientCases.AsNoTracking().Where(row => ids.Contains(row.Id)).ToArrayAsync(), row => { Assert.Equal(123.45m, row.CourtClaimAmount); Assert.Equal("Исходная история", row.EventHistory); });
    }
}
