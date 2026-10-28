using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static async Task<CashOrdersController> CreateCashOrderAdminControllerAsync(PaymentContext db,
        IInvoiceDocumentPdfService? pdfService = null, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        var actor = await CreateUserAsync(db, $"cash-legacy-{Guid.NewGuid():N}@fixture.local", "Кассир синтетической проверки", roleName: "admin");
        return new(db, pdfService, configuration) { ControllerContext = CreateController(db, actor.Id).ControllerContext };
    }

    private static CashOrdersController CompanyCashController(PaymentContext db, int userId)
        => new(db) { ControllerContext = CreateController(db, userId, isAdminClaim: true).ControllerContext };

    [Theory]
    [InlineData(false, CashOrderType.Incoming)]
    [InlineData(false, CashOrderType.Outgoing)]
    [InlineData(true, CashOrderType.Incoming)]
    [InlineData(true, CashOrderType.Outgoing)]
    public async Task CashCompany_DeletedDraftKeepsItsNumberReserved(bool postgres, CashOrderType type)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanCreate, true));
        var prefix = type == CashOrderType.Incoming ? "ПКО" : "РКО";
        var reserved = new CashOrder { Number = $"{prefix}-{BankScopeDate:yyyy}-0001", Type = type, Status = CashOrderStatus.Draft, Date = BankScopeDate, Amount = 100m };
        db.CashOrders.Add(reserved);
        await db.SaveChangesAsync();
        db.CashOrders.Remove(reserved);
        await db.SaveChangesAsync();
        Assert.False(await db.CashOrders.AnyAsync());
        var controller = CompanyCashController(db, seed.Actor.Id);
        var proposed = Assert.IsType<OkObjectResult>(await controller.GetNextNumber(type, BankScopeDate, default));
        var number = System.Text.Json.JsonSerializer.SerializeToElement(proposed.Value).GetProperty("number").GetString();
        Assert.Equal($"{prefix}-{BankScopeDate:yyyy}-0002", number);
        var created = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.Create(
            new CashOrder { Type = type, Date = BankScopeDate, Amount = 100m, Status = CashOrderStatus.Draft }, default)).Value);
        Assert.Equal(number, created.Number);
        Assert.Equal(2, await db.CashOrders.IgnoreQueryFilters().CountAsync());
        Assert.Equal(reserved.Number, await db.CashOrders.IgnoreQueryFilters().Where(item => item.Id == reserved.Id).Select(item => item.Number).SingleAsync());
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCompany_UnknownUserCannotUseAnAdministratorClaim(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var seed = await SeedAccountScopeAsync(fixture.Db, admin: false);
        var own = await SeedCompanyCashOrderAsync(fixture.Db, seed, foreign: false, paymentSource: false);
        var before = await CaptureManualCompanyAsync(fixture.Db);
        Assert.IsType<ForbidResult>(await CompanyCashController(fixture.Db, int.MaxValue).GetById(own.Id, default));
        Assert.Equal(before, await CaptureManualCompanyAsync(fixture.Db));
    }

    private static async Task<CashOrder> SeedCompanyCashOrderAsync(PaymentContext db, AccountScopeSeed seed,
        bool foreign, bool paymentSource, CashOrderStatus status = CashOrderStatus.Posted)
    {
        var order = new CashOrder
        {
            Number = foreign ? "FOREIGN-SECRET" : "OWN-ORDER",
            Date = BankScopeDate,
            Type = CashOrderType.Incoming,
            Amount = foreign ? 900m : 100m,
            Account = "Одинаковая касса",
            Status = status,
            CreatedByUserId = seed.Actor.Id,
        };
        if (paymentSource)
        {
            order.Payment = new Payment
            {
                Date = BankScopeDate,
                PlannedDate = BankScopeDate,
                Amount = order.Amount,
                PaidAmount = order.Amount,
                Status = PaymentStatus.Completed,
                Type = PaymentType.Income,
                SellerCompanyId = foreign ? seed.Other.Id : seed.Company.Id,
                Description = "Синтетическая наличная оплата",
                Notes = "",
                SystemNotes = "",
                PaymentSource = new PaymentSource { Name = "Наличные для кассовой проверки" },
            };
        }
        else
        {
            order.MoneyOperation = new MoneyOperation
            {
                Date = BankScopeDate,
                Amount = order.Amount,
                Source = MoneyOperationSource.Cash,
                AccountingType = MoneyOperationAccountingType.Fact,
                Direction = MoneyOperationDirection.Income,
                MoneyAccount = new MoneyAccount
                {
                    CompanyId = foreign ? seed.Other.Id : seed.Company.Id,
                    Name = "Одинаковая касса",
                    Source = MoneyOperationSource.Cash,
                    Currency = "RUB",
                    IsActive = true,
                },
                ResponsibleUserId = seed.Actor.Id,
            };
        }
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CashCompany_ListsAndCashBookUseAssignedCompany(bool postgres, bool paymentSource)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        var own = await SeedCompanyCashOrderAsync(db, seed, foreign: false, paymentSource);
        await SeedCompanyCashOrderAsync(db, seed, foreign: true, paymentSource);
        var controller = CompanyCashController(db, seed.Actor.Id);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>((await controller.GetRegister(
            null, null, null, null, null, accountName: "Одинаковая касса")).Result).Value);
        Assert.Equal(own.Id, Assert.Single(register.Items).Id);
        Assert.Equal(1, register.Total);
        Assert.Equal(100m, register.IncomingAmount);
        var paged = Assert.IsType<PagedResponse<CashOrderDto>>(Assert.IsType<OkObjectResult>((await controller.GetPaged(
            null, null, null, null, null)).Result).Value);
        Assert.Equal(own.Id, Assert.Single(paged.Items).Id);
        Assert.Equal(1, paged.Total);
        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>((await controller.GetCashBook(
            BankScopeDate, BankScopeDate)).Result).Value);
        Assert.Equal(100m, cashBook.Income);
        Assert.Equal(100m, cashBook.ClosingBalance);
        Assert.Equal(1, cashBook.IncomingCount);
        Assert.Equal(own.Id, Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(
            await controller.GetById(own.Id, default)).Value).Id);
        Assert.Contains("OWN-ORDER", Assert.IsType<ContentResult>(await controller.Print(own.Id, default)).Content);
    }

    public static IEnumerable<object[]> CashCompanyDirectCases =>
        from postgres in new[] { false, true }
        from paymentSource in new[] { false, true }
        from endpoint in new[] { "get", "print", "pdf", "word", "update", "delete", "post", "cancel", "copy", "from-source" }
        select new object[] { postgres, paymentSource, endpoint };

    [Theory]
    [MemberData(nameof(CashCompanyDirectCases))]
    public async Task CashCompany_ForeignOrderCannotBeReadOrChanged(bool postgres, bool paymentSource, string endpoint)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.CanCreate, true).SetProperty(item => item.CanEdit, true).SetProperty(item => item.CanDelete, true));
        var foreign = await SeedCompanyCashOrderAsync(db, seed, foreign: true, paymentSource, CashOrderStatus.Draft);
        var before = await CaptureManualCompanyAsync(db);
        var controller = CompanyCashController(db, seed.Actor.Id);
        var result = endpoint switch
        {
            "get" => await controller.GetById(foreign.Id, default),
            "print" => await controller.Print(foreign.Id, default),
            "pdf" => await controller.DownloadPdf(foreign.Id, default),
            "word" => await controller.DownloadWord(foreign.Id, default),
            "update" => await controller.Update(foreign.Id, new CashOrder { Amount = 999m }, default),
            "delete" => await controller.Delete(foreign.Id, default),
            "post" => await controller.Post(foreign.Id, default),
            "cancel" => await controller.Cancel(foreign.Id, null, default),
            "copy" => await controller.Copy(foreign.Id, default),
            _ => paymentSource ? await controller.CreateFromPayment(foreign.PaymentId!.Value, default)
                : await controller.CreateFromMoneyOperation(foreign.MoneyOperationId!.Value, default),
        };
        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
        db.ChangeTracker.Clear();
        var unchanged = await db.CashOrders.SingleAsync(item => item.Id == foreign.Id);
        Assert.Equal(CashOrderStatus.Draft, unchanged.Status);
        Assert.Equal(900m, unchanged.Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCompany_PaymentPrintDoesNotBypassCompanyScope(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        var foreign = await SeedCompanyCashOrderAsync(db, seed, foreign: true, paymentSource: true);
        var before = await CaptureManualCompanyAsync(db);
        Assert.IsType<NotFoundResult>(await CompanyCashController(db, seed.Actor.Id).PrintForPayment(foreign.PaymentId!.Value, default));
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CashCompany_AllowedLifecyclePreservesMoneyFacts(bool postgres, bool paymentSource)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.CanCreate, true).SetProperty(item => item.CanEdit, true).SetProperty(item => item.CanDelete, true));
        var own = await SeedCompanyCashOrderAsync(db, seed, foreign: false, paymentSource, CashOrderStatus.Draft);
        if (!paymentSource)
        {
            // A valid source operation has an explicit currency; unknown historical currency must remain rejected.
            own.MoneyOperation!.Currency = "RUB";
            await db.SaveChangesAsync();
        }
        var controller = CompanyCashController(db, seed.Actor.Id);
        var model = new CashOrder
        {
            Date = own.Date,
            Amount = own.Amount,
            Type = own.Type,
            Currency = own.Currency,
            MoneyOperationId = own.MoneyOperationId,
            PaymentId = own.PaymentId,
            Account = own.Account,
            Comment = "Описание своей операции",
        };
        var updated = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(await controller.Update(own.Id, model, default)).Value);
        Assert.Equal("Описание своей операции", updated.Comment);
        Assert.Equal(updated.Comment, await db.CashOrders.AsNoTracking().Where(item => item.Id == own.Id).Select(item => item.Comment).SingleAsync());
        Assert.Equal(CashOrderStatus.Posted, Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(
            await controller.Post(own.Id, default)).Value).Status);
        Assert.Equal(CashOrderStatus.Posted, await db.CashOrders.AsNoTracking().Where(item => item.Id == own.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(CashOrderStatus.Cancelled, Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(
            await controller.Cancel(own.Id, new CashOrderCancelRequest { Reason = "Синтетическая отмена" }, default)).Value).Status);
        Assert.Equal(CashOrderStatus.Cancelled, await db.CashOrders.AsNoTracking().Where(item => item.Id == own.Id).Select(item => item.Status).SingleAsync());
        var copied = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.Copy(own.Id, default)).Value);
        Assert.Equal(CashOrderStatus.Draft, copied.Status);
        Assert.IsType<NoContentResult>(await controller.Delete(copied.Id, default));
        Assert.False(await db.CashOrders.AnyAsync(item => item.Id == copied.Id));
        var fromSource = paymentSource ? await controller.CreateFromPayment(own.PaymentId!.Value, default)
            : await controller.CreateFromMoneyOperation(own.MoneyOperationId!.Value, default);
        var created = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(fromSource).Value);
        Assert.Equal(100m, created.Amount);
        Assert.NotEqual(copied.Number, created.Number);
        Assert.True(await db.CashOrders.IgnoreQueryFilters().AnyAsync(item => item.Id == copied.Id && item.Number == copied.Number));
        if (paymentSource)
        {
            Assert.Equal(100m, await db.Payments.Where(item => item.Id == own.PaymentId).Select(item => item.PaidAmount).SingleAsync());
            Assert.Equal(1, await db.Payments.CountAsync());
            Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        }
        else
        {
            Assert.Equal(100m, await db.MoneyOperations.Where(item => item.Id == own.MoneyOperationId).Select(item => item.Amount).SingleAsync());
            Assert.Equal("RUB", await db.MoneyOperations.Where(item => item.Id == own.MoneyOperationId).Select(item => item.Currency).SingleAsync());
            Assert.Equal(1, await db.MoneyOperations.CountAsync());
            Assert.Empty(await db.Payments.ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CashCompany_AdministratorKeepsAccessToBothCompanies(bool postgres, bool paymentSource)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: true);
        var own = await SeedCompanyCashOrderAsync(db, seed, foreign: false, paymentSource);
        var foreign = await SeedCompanyCashOrderAsync(db, seed, foreign: true, paymentSource);
        var controller = CompanyCashController(db, seed.Actor.Id);
        var result = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>((await controller.GetRegister(
            null, null, null, null, null)).Result).Value);
        Assert.Equal(new[] { own.Id, foreign.Id }.Order(), result.Items.Select(item => item.Id).Order());
        Assert.Equal(1000m, result.IncomingAmount);
        Assert.Equal(foreign.Id, Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(await controller.GetById(foreign.Id, default)).Value).Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CashCompany_CompanyAccessIsFreshForEveryRequest(bool postgres, bool paymentSource)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedAccountScopeAsync(db, admin: false);
        var own = await SeedCompanyCashOrderAsync(db, seed, foreign: false, paymentSource);
        var controller = CompanyCashController(db, seed.Actor.Id);
        Assert.IsType<OkObjectResult>(await controller.GetById(own.Id, default));
        await db.UserCompanyAccesses.Where(item => item.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        var before = await CaptureManualCompanyAsync(db);
        Assert.IsType<NotFoundResult>(await controller.GetById(own.Id, default));
        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>((await controller.GetCashBook(
            BankScopeDate, BankScopeDate)).Result).Value);
        Assert.Equal(0m, cashBook.ClosingBalance);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }
}
