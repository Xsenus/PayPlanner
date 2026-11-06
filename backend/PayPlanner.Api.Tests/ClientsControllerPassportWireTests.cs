using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ClientsControllerTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("detail")]
    [InlineData("update")]
    public async Task PassportWire_RootProfileReplyOmitsLocallyGeneratedCrypto(string action)
    {
        await using var db = await PassportWireTestSupport.ContextAsync(true); var actor = await CreateUserAsync(db, "admin"); var controller = CreateController(db, actor);
        Client Input(string series) => new() { Name = "Синтетический профиль паспорта", Notes = "__PP_CLIENT_PROFILE_V2__:" + System.Text.Json.JsonSerializer.Serialize(new { passportSeries = series, passportNumber = "123456", passportIssuedBy = "Синтетический отдел", passportIssueDate = "2020-01-02T00:00:00Z", passportDepartmentCode = "540-001", birthPlacePassport = "Тестовый город", birthPlaceFias = "Тестовый ФИАС" }) };
        var initial = Assert.IsType<CreatedResult>(await controller.Create(Input("4510"), CancellationToken.None)); var id = Assert.IsType<Client>(initial.Value).Id; object? reply = initial.Value;
        if (action == "detail") reply = Assert.IsType<OkObjectResult>(await controller.GetById(id, CancellationToken.None)).Value;
        if (action == "update") reply = Assert.IsType<OkObjectResult>(await controller.Update(id, Input("4511"), CancellationToken.None)).Value;
        var stored = await db.ClientPassports.AsNoTracking().SingleAsync(); Assert.Equal(id, stored.ClientId); Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.NotNull(PassportWireTestSupport.Property(name).GetValue(stored))); Assert.Equal(action == "update" ? "4511" : "4510", await PassportWireTestSupport.Encryptor().DecryptAsync(stored.SeriesEncrypted!, $"ClientPassport:Series:{stored.Id}", CancellationToken.None));
        PassportWireTestSupport.AssertSafe(reply); var response = Assert.IsType<Client>(reply);
        // The fresh root detail query does not load the separately protected
        // passport collection through a previous request's tracked entities.
        if (action == "detail") Assert.Empty(response.Passports);
        else
        {
            Assert.Equal(stored.Series, Assert.Single(response.Passports).Series);
            Assert.Equal(stored.Number, Assert.Single(response.Passports).Number);
        }
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(1, await db.Clients.CountAsync());
    }
}
