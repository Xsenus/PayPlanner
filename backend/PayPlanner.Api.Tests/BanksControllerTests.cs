using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BanksControllerTests
{
    [Fact]
    public async Task CreateUpdateDeleteBank_RoundTripsSuccessfully()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, 51);

        var createAction = await controller.CreateBank(
            new BankUpsertRequest
            {
                Name = "ООО \"Банк Точка\"",
                Bik = "044525104",
                CorrespondentAccount = "30101810745374525104",
                City = "Москва",
                IsForeign = false,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(createAction.Result);
        var createdDto = Assert.IsType<BankDto>(created.Value);
        Assert.Equal("ООО \"Банк Точка\"", createdDto.Name);

        var listAction = await controller.GetBanks(null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var banks = Assert.IsAssignableFrom<IReadOnlyCollection<BankDto>>(listOk.Value);
        Assert.Single(banks);

        var updateAction = await controller.UpdateBank(
            createdDto.Id,
            new BankUpsertRequest
            {
                Name = "ООО \"Банк Точка\"",
                Bik = "044525104",
                CorrespondentAccount = "30101810745374525104",
                City = "Самара",
                IsForeign = false,
            },
            CancellationToken.None);

        var updatedOk = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<BankDto>(updatedOk.Value);
        Assert.Equal("Самара", updatedDto.City);

        var deleteAction = await controller.DeleteBank(createdDto.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteAction);

        Assert.False(await db.Banks.AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteBank_ReturnsConflict_WhenBankUsedInLegalEntityRequisites()
    {
        await using var db = await CreateContextAsync();
        var bank = new Bank
        {
            Name = "Тестовый банк",
            Bik = "123456789",
            CreatedAt = DateTime.UtcNow,
        };

        db.Banks.Add(bank);
        await db.SaveChangesAsync();

        db.LegalEntities.Add(new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "ООО Тест",
            CreatedAt = DateTime.UtcNow,
            BankAccountsJson = $"[{{\"bankId\":{bank.Id},\"bank\":\"Тестовый банк\",\"accountNumber\":\"40802810000000000001\"}}]",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, 52);
        var deleteAction = await controller.DeleteBank(bank.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(deleteAction);
        Assert.Equal("Банк используется в реквизитах юридических лиц и не может быть удален.", conflict.Value);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static BanksController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый администратор"),
        };

        var controller = new BanksController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }
}
