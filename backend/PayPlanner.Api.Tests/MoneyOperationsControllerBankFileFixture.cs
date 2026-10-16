using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static async Task<BankImportCaseController> CreateBankImportControllerAsync(
        PaymentContext db, int? userId = null, bool isAdminClaim = false, string? selectedBankAccountNumber = null)
    {
        // These legacy import examples are synthetic. Explicitly provide the
        // company/account/employee required by the new public upload contract.
        var actor = userId.HasValue
            ? await db.Users.Include(user => user.Role).SingleAsync(user => user.Id == userId)
            : await CreateUserAsync(db, "bank-import-case@example.test", "Синтетический оператор импорта", roleName: "bank-import-fixture");
        actor.IsEmployee = true;
        Assert.True(actor.IsActive && actor.IsApproved);
        foreach (var section in new[] { RolePermissionCatalog.Payments, RolePermissionCatalog.Accounts })
        {
            var permission = await db.RolePermissions.SingleOrDefaultAsync(item => item.RoleId == actor.RoleId && item.Section == section);
            if (permission is null)
            {
                permission = new RolePermission { RoleId = actor.RoleId, Section = section };
                db.RolePermissions.Add(permission);
            }
            permission.CanView = true;
            permission.CanCreate = true;
            permission.CanEdit = true;
            permission.ViewScope = DataVisibilityScope.All;
        }
        var company = new LegalEntity { ShortName = "Синтетическая компания импорта" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        Assert.False(await db.SystemSettings.AnyAsync(item => item.Key == "legalEntities.mainCompanyIds"));
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id }) });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor.Id, LegalEntityId = company.Id });
        foreach (var payment in await db.Payments.Where(item => item.SellerCompanyId == null).ToListAsync()) payment.SellerCompanyId = company.Id;
        var accounts = await db.MoneyAccounts.Where(item => item.Source == MoneyOperationSource.Bank && item.IsActive).ToListAsync();
        Assert.True(accounts.Count <= 1, "A multi-account example must choose its account explicitly.");
        var account = accounts.SingleOrDefault();
        if (account is null)
        {
            account = new MoneyAccount { Name = "Расчетный счет", Source = MoneyOperationSource.Bank, Currency = "RUB", IsActive = true };
            db.MoneyAccounts.Add(account);
        }
        Assert.Null(account.CompanyId);
        account.CompanyId = company.Id;
        if (selectedBankAccountNumber is not null)
        {
            account.Name = selectedBankAccountNumber;
            account.BankAccountNumber = selectedBankAccountNumber;
        }
        await db.SaveChangesAsync();
        return new BankImportCaseController(db, company.Id, account.Id)
        {
            ControllerContext = CreateController(db, actor.Id, isAdminClaim).ControllerContext,
        };
    }

    [NonController]
    private sealed class BankImportCaseController(PaymentContext db, int ownCompanyId, int ownAccountId)
        : MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
    {
        public new Task<ActionResult<BankStatementImportResultDto>> ImportBankStatement(
            IFormFile? file, CancellationToken ct, int? companyId = null, int? moneyAccountId = null)
            => base.ImportBankStatement(file, ct, companyId ?? ownCompanyId, moneyAccountId ?? ownAccountId);
    }
}
