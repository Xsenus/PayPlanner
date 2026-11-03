using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> ClientBirthDateNullCases()
        => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from kind in new[] { "null", "spaces", "absent", "null-profile", "null-conflict" } select new object[] { route, create, kind };

    [Theory]
    [MemberData(nameof(ClientBirthDateNullCases))]
    public async Task ClientBirthDate_BoundaryHttpDistinguishesExplicitClearFromMissingDate(int route, bool create, string kind)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, owner) = await SeedBirthdayClientAsync(db);
        var before = await db.Clients.AsNoTracking().SingleAsync();
        var input = new Client { Name = "Проверка границы даты", ResponsibleUserId = actor.Id };
        input.Notes = kind switch
        {
            "spaces" => BirthdayNotes("   "),
            "absent" => BirthdayProfilePrefix + JsonSerializer.Serialize(new { registrationAddress = "Прежний адрес", passportSeries = "OLD", passportNumber = "000111" }),
            "null-profile" => BirthdayProfilePrefix + "null",
            _ => BirthdayNotes(null),
        };
        if (kind == "null-conflict") input.BirthDate = new DateTime(1990, 1, 2);
        using var server = CaseWriteServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        var path = route == 0 ? "/api/clients" : $"/api/v{route}/clients";
        using var response = create ? await http.PostAsJsonAsync(path, input) : await http.PutAsJsonAsync($"{path}/{owner}", input);
        if (kind == "null-conflict")
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var old = await db.Clients.AsNoTracking().SingleAsync();
            Assert.Equal(before.BirthDate, old.BirthDate); Assert.Equal(before.Name, old.Name); Assert.Equal(before.Notes, old.Notes);
            Assert.False(db.ChangeTracker.HasChanges());
        }
        else
        {
            Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var id = json.RootElement.GetProperty("id").GetInt32();
            var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == id);
            DateTime? expected = !create && kind is "absent" or "null-profile" ? OldClientBirthday : null;
            Assert.Equal(expected, saved.BirthDate); Assert.Equal(input.Notes, saved.Notes); Assert.Equal(input.Name, saved.Name);
            Assert.Equal(create ? 2 : 1, await db.Clients.CountAsync());
            if (create) Assert.Equal(before.BirthDate, await db.Clients.Where(row => row.Id == owner).Select(row => row.BirthDate).SingleAsync());
        }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }
}
