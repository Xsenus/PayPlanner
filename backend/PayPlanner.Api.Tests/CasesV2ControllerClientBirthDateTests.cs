using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private const string BirthdayProfilePrefix = "__PP_CLIENT_PROFILE_V2__:";
    private static readonly DateTime OldClientBirthday = new(1980, 5, 12);

    private static async Task<(User Actor, int[] Cases, int ClientId)> SeedBirthdayClientAsync(PaymentContext db)
    {
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        var client = await db.Clients.SingleAsync(); client.BirthDate = OldClientBirthday; client.Notes = BirthdayNotes("1980-05-12");
        db.AddressRecords.Add(new AddressRecord { ClientId = client.Id, Value = "Прежний адрес", IsPrimary = true });
        db.ClientPassports.Add(new ClientPassport { ClientId = client.Id, Series = "OLD", Number = "000111", IsPrimary = true });
        await db.SaveChangesAsync(); return (actor, ids, client.Id);
    }

    private static string BirthdayNotes(object? value) => BirthdayProfilePrefix + JsonSerializer.Serialize(new
    {
        birthDate = value,
        registrationAddress = "Прежний адрес",
        passportSeries = "OLD",
        passportNumber = "000111",
    });

    private static Task<IActionResult> WriteBirthdayClientAsync(PaymentContext db, User actor, int id, Client input, int route, bool create)
    {
        if (create) return CreateAtomicClientAsync(db, actor, input, route);
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id);
        return route switch
        {
            0 => root.Update(id, input, CancellationToken.None),
            1 => new ClientsV1Controller(db) { ControllerContext = root.ControllerContext }.Update(id, input, CancellationToken.None),
            _ => new ClientsV2Controller(db) { ControllerContext = root.ControllerContext }.Update(id, input, CancellationToken.None),
        };
    }

    public static IEnumerable<object[]> ClientBirthDateRejectionCases()
    {
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var create in new[] { false, true })
                foreach (var kind in new[] { "conflict", "invalid-date", "invalid-type", "duplicate", "malformed" }) yield return new object[] { route, create, false, kind };
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var create in new[] { false, true })
                foreach (var kind in new[] { "conflict", "invalid-date" }) yield return new object[] { route, create, true, kind };
    }

    [Theory]
    [MemberData(nameof(ClientBirthDateRejectionCases))]
    public async Task ClientBirthDate_HttpRejectsConflictingOrMalformedDatesWithoutAnyWrite(int route, bool create, bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids, owner) = await SeedBirthdayClientAsync(db); var old = await db.Clients.AsNoTracking().SingleAsync(); var addressId = await db.AddressRecords.Select(row => row.Id).SingleAsync(); var passportId = await db.ClientPassports.Select(row => row.Id).SingleAsync();
        var input = new Client { Name = "Не сохранять частичные изменения", Phone = "+79999999999", Email = "changed@example.local", ResponsibleUserId = actor.Id };
        input.Notes = kind switch
        {
            "invalid-date" => BirthdayNotes("2000-02-31"),
            "invalid-type" => BirthdayNotes(42),
            "duplicate" => BirthdayProfilePrefix + "{\"birthDate\":\"1990-01-02\",\"BIRTHDATE\":\"1991-01-02\"}",
            "malformed" => BirthdayProfilePrefix + "{bad",
            _ => BirthdayNotes("1990-01-02"),
        };
        if (kind == "conflict") input.BirthDate = new DateTime(1991, 1, 2);
        using var server = CaseWriteServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        var path = route == 0 ? "/api/clients" : $"/api/v{route}/clients";
        using var response = create ? await http.PostAsJsonAsync(path, input) : await http.PutAsJsonAsync($"{path}/{owner}", input);
        var stored = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == owner);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest && await db.Clients.CountAsync() == 1 && stored.Notes == old.Notes && stored.BirthDate == old.BirthDate && stored.Name == old.Name,
            $"HTTP={(int)response.StatusCode}; kind={kind}; clients={await db.Clients.CountAsync()}; oldDate={old.BirthDate:yyyy-MM-dd}; storedDate={stored.BirthDate:yyyy-MM-dd}; oldNotesPreserved={stored.Notes == old.Notes}; namePreserved={stored.Name == old.Name}");
        Assert.Equal(old.Phone, stored.Phone); Assert.Equal(old.Email, stored.Email); Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(addressId, await db.AddressRecords.Select(row => row.Id).SingleAsync()); Assert.Equal(passportId, await db.ClientPassports.Select(row => row.Id).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientBirthDatePositiveCases()
        => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from postgres in new[] { false, true } from kind in new[] { "profile", "raw", "same", "clear" } select new object[] { route, create, postgres, kind };

    [Theory]
    [MemberData(nameof(ClientBirthDatePositiveCases))]
    public async Task ClientBirthDate_StoresOneCalendarDateForBothWriteSourcesAndExplicitClear(int route, bool create, bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids, owner) = await SeedBirthdayClientAsync(db); var input = new Client { Name = "Клиент с календарной датой", ResponsibleUserId = actor.Id };
        DateTime? expected = kind == "clear" ? null : kind == "raw" ? new DateTime(1945, 3, 15) : new DateTime(2000, 2, 29);
        input.Notes = kind == "raw" ? "Обычные заметки без профиля" : BirthdayNotes(kind == "clear" ? "" : "2000-02-29");
        if (kind is "raw" or "same") input.BirthDate = DateTime.SpecifyKind(expected!.Value.AddHours(12), DateTimeKind.Utc);
        var result = await WriteBirthdayClientAsync(db, actor, owner, input, route, create);
        var value = create ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value; var reply = Assert.IsType<Client>(value);
        var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == reply.Id);
        Assert.True(saved.BirthDate == expected, $"source={kind}; stored={saved.BirthDate:O}; expected={expected:O}"); Assert.Equal(expected, reply.BirthDate); Assert.Equal(input.Notes, saved.Notes); Assert.Equal(input.Name, saved.Name);
        if (saved.BirthDate.HasValue) Assert.Equal(TimeSpan.Zero, saved.BirthDate.Value.TimeOfDay);
        Assert.Equal(create ? 2 : 1, await db.Clients.CountAsync()); Assert.Equal("Прежний адрес", (await db.AddressRecords.AsNoTracking().FirstAsync()).Value); Assert.Equal("000111", (await db.ClientPassports.AsNoTracking().FirstAsync()).Number);
        if (create) Assert.Equal(OldClientBirthday, await db.Clients.Where(row => row.Id == owner).Select(row => row.BirthDate).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientBirthDate_UpdateWithoutADateKeepsExistingStoredBirthday(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids, owner) = await SeedBirthdayClientAsync(db); var input = new Client { Name = "Только заметки", Notes = "Новые обычные заметки", ResponsibleUserId = actor.Id };
        var result = Assert.IsType<OkObjectResult>(await WriteBirthdayClientAsync(db, actor, owner, input, route, create: false));
        Assert.Equal(OldClientBirthday, Assert.IsType<Client>(result.Value).BirthDate); Assert.Equal(OldClientBirthday, await db.Clients.Where(row => row.Id == owner).Select(row => row.BirthDate).SingleAsync());
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientBirthDate_VersionedProfileDateReachesBirthdaySchedulerWithoutChangingOldTasks(int route, bool create)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, owner) = await SeedBirthdayClientAsync(db);
        var input = new Client { Name = "Синтетический именинник", Notes = BirthdayNotes("1990-09-28"), ResponsibleUserId = actor.Id };
        var result = await WriteBirthdayClientAsync(db, actor, owner, input, route, create);
        var id = Assert.IsType<Client>(create ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value).Id;
        await AssertBulkCasesUnchangedAsync(db, ids);
        var oldTasks = await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status, row.Result, row.ClientCaseId }).ToArrayAsync();
        db.SystemSettings.AddRange(new SystemSetting { Key = ClientBirthdayTaskSchedulerService.EnabledSettingKey, Value = "true" }, new SystemSetting { Key = "tasks.automation.caseControl.scheduleTimeZone", Value = "UTC" }); await db.SaveChangesAsync();
        var services = new ServiceCollection(); services.AddDbContext<PaymentContext>(options => options.UseSqlite(db.Database.GetDbConnection()));
        await using var provider = services.BuildServiceProvider(); using var scheduler = new ClientBirthdayTaskSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<ClientBirthdayTaskSchedulerService>.Instance);
        var now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc); var first = await scheduler.RunOnceAsync(now); var again = await scheduler.RunOnceAsync(now.AddHours(1));
        Assert.Equal(1, first.CreatedCount); Assert.Equal(0, again.CreatedCount);
        var birthday = await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.TagsJson.Contains("auto-birthday")); Assert.Contains($"birthday:{id}:2026", birthday.TagsJson); Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), birthday.DueAt);
        var preserved = await db.TaskJournalItems.AsNoTracking().Where(row => !row.TagsJson.Contains("auto-birthday")).OrderBy(row => row.Id).Select(row => new { row.Id, row.Status, row.Result, row.ClientCaseId }).ToArrayAsync(); Assert.Equal(oldTasks, preserved);
    }
}
