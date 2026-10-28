using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrdersControllerTests
{
    [Fact]
    public async Task CashBook_UsesOneAggregateQueryAndSkipsTimeZoneForExplicitPeriod()
    {
        var recorder = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(recorder);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-2001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 7, 1),
                Amount = 1_000m,
                Account = "Основная касса",
            },
            new CashOrder
            {
                Number = "РКО-2026-2001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 7, 2),
                Amount = 250m,
                Account = "Основная касса",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, userId, isAdminClaim: false);

        recorder.Commands.Clear();
        var action = await controller.GetCashBook(
            from: new DateTime(2026, 7, 2),
            to: new DateTime(2026, 7, 3),
            accountName: "Основная касса",
            ct: CancellationToken.None);

        var response = Assert.IsType<CashBookResponseDto>(
            Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1_000m, response.OpeningBalance);
        Assert.Equal(0m, response.Income);
        Assert.Equal(250m, response.Expense);
        Assert.Equal(750m, response.ClosingBalance);
        Assert.Equal(2, response.Days.Count);
        Assert.Single(recorder.Commands, sql => sql.Contains("FROM \"CashOrders\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            recorder.Commands,
            sql => sql.Contains("FROM \"UserSettings\"", StringComparison.Ordinal));

        recorder.Commands.Clear();
        await controller.GetCashBook(
            from: null,
            to: null,
            accountName: "Основная касса",
            ct: CancellationToken.None);
        Assert.Single(recorder.Commands, sql => sql.Contains("FROM \"UserSettings\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("USD", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("USD", true)]
    public async Task CashBook_RejectsUnconfirmedRubleTotalsIncludingOpeningBalance(string? currency, bool beforePeriod)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9100",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 100m,
                Account = "Касса",
                Currency = "RUB"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9101",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = beforePeriod ? new DateTime(2026, 4, 30) : new DateTime(2026, 5, 2),
                Amount = 20m,
                Account = "Касса",
                Currency = currency
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        var from = new DateTime(2026, 5, 1);
        var to = new DateTime(2026, 5, 3);

        var book = await controller.GetCashBook(from, to, "Касса", ct: default);
        var print = await controller.PrintCashBook(from, to, "Касса", ct: default);

        Assert.IsType<BadRequestObjectResult>(book.Result);
        Assert.IsType<BadRequestObjectResult>(print);
        Assert.Equal(2, await db.CashOrders.CountAsync());
    }

    [Theory]
    [InlineData("RUB", true)]
    [InlineData("USD", true)]
    [InlineData("=1+1", true)]
    [InlineData(null, false)]
    public async Task CashBookExport_RequiresOneConfirmedCurrency(string? currency, bool allowed)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        var user = await db.Users.Include(item => item.Role).SingleAsync(item => item.Id == userId);
        user.Role!.Name = "admin";
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9110",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 30),
            Amount = 100m,
            Account = "Касса",
            Currency = currency
        });
        await db.SaveChangesAsync();
        var controller = new ReportsController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportsController>.Instance)
        {
            ControllerContext = CreateController(db, userId, isAdminClaim: true).ControllerContext,
        };

        var result = await controller.ExportManagementReportCsv("cash", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3),
            null, "Касса", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, default);

        if (allowed)
        {
            var file = Assert.IsType<FileContentResult>(result);
            var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
            Assert.Contains(currency!, text);
            if (currency == "=1+1") Assert.True(text.Contains("Код: =1+1", StringComparison.Ordinal), text);
        }
        else Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(1, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CashBook_SingleForeignCurrencyIncludesOpeningAndPrintsCorrectly()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9170",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 30),
                Amount = 100m,
                Account = "Касса",
                Currency = " usd "
            },
            new CashOrder
            {
                Number = "РКО-2026-9171",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 20m,
                Account = "Касса",
                Currency = "USD"
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var from = new DateTime(2026, 5, 1);
        var to = new DateTime(2026, 5, 3);

        var result = await controller.GetCashBook(from, to, "Касса", ct: default);
        var book = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("single", book.CurrencyStatus);
        Assert.Equal("USD", book.Currency);
        Assert.Equal(100m, book.OpeningBalance);
        Assert.Equal(20m, book.Expense);
        Assert.Equal(80m, book.ClosingBalance);
        var print = Assert.IsType<ContentResult>(await controller.PrintCashBook(from, to, "Касса", ct: default));
        Assert.Contains("100,00 USD", print.Content);
        Assert.Contains("80,00 USD", print.Content);
        Assert.DoesNotContain("80,00 руб.", print.Content);
    }

    [Theory]
    [InlineData("USD", CashOrderStatus.Posted)]
    [InlineData(null, CashOrderStatus.Draft)]
    public async Task LegacyCashClient_CannotReadUnconfirmedCurrencyAsRubles(string? currency, CashOrderStatus status)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9180",
            Type = CashOrderType.Incoming,
            Status = status,
            Date = new DateTime(2026, 5, 2),
            Amount = 100m,
            Currency = currency
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var register = await controller.GetRegister(null, null, null, null, null, ct: default);
        Assert.IsType<ConflictObjectResult>(register.Result);
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Contains("X-PayPlanner-Currency-Contract", controller.Response.Headers.Vary.ToString());
        if (status == CashOrderStatus.Posted)
        {
            var book = await controller.GetCashBook(new DateTime(2026, 5, 1), new DateTime(2026, 5, 3), ct: default);
            Assert.IsType<ConflictObjectResult>(book.Result);
        }
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "2";
        Assert.IsType<ConflictObjectResult>((await controller.GetRegister(null, null, null, null, null, ct: default)).Result);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        Assert.IsType<OkObjectResult>((await controller.GetRegister(null, null, null, null, null, ct: default)).Result);
        if (status == CashOrderStatus.Posted)
            Assert.IsType<OkObjectResult>((await controller.GetCashBook(new DateTime(2026, 5, 1), new DateTime(2026, 5, 3), ct: default)).Result);
    }

    [Fact]
    public async Task LegacyCashClient_CanReadKnownRubleAmounts()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9181",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 2),
            Amount = 100m,
            Currency = "RUB"
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        Assert.IsType<OkObjectResult>((await controller.GetRegister(null, null, null, null, null, ct: default)).Result);
        Assert.IsType<OkObjectResult>((await controller.GetCashBook(new DateTime(2026, 5, 1), new DateTime(2026, 5, 3), ct: default)).Result);
    }

    [Fact]
    public async Task CashBookExport_RejectsMixedCurrenciesAcrossOpeningAndPeriod()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        var user = await db.Users.Include(item => item.Role).SingleAsync(item => item.Id == userId);
        user.Role!.Name = "admin";
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9172",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 30),
                Amount = 100m,
                Currency = "USD"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9173",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 20m,
                Currency = "RUB"
            });
        await db.SaveChangesAsync();
        var controller = new ReportsController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportsController>.Instance)
        {
            ControllerContext = CreateController(db, userId, isAdminClaim: true).ControllerContext,
        };

        var result = await controller.ExportManagementReportCsv("cash", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3),
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(2, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CashBookExport_ExcludesOtherCompanyOrdersBeforeCurrencyCheckAndTotals()
    {
        await using var db = await CreateContextAsync();
        var own = new LegalEntity { ShortName = "Своя" };
        var foreign = new LegalEntity { ShortName = "Другая" };
        db.LegalEntities.AddRange(own, foreign);
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.IsEmployee = true;
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Reports,
            CanView = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = own.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) });
        var ownOperation = new MoneyOperation
        {
            MoneyAccount = new MoneyAccount { Name = "Своя касса", Source = MoneyOperationSource.Cash, CompanyId = own.Id },
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 2),
            Amount = 100m,
            Currency = "RUB"
        };
        var foreignOperation = new MoneyOperation
        {
            MoneyAccount = new MoneyAccount { Name = "Чужая касса", Source = MoneyOperationSource.Cash, CompanyId = foreign.Id },
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 2),
            Amount = 900m,
            Currency = "USD"
        };
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9120",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = ownOperation.Date,
                Amount = 100m,
                Currency = "RUB",
                MoneyOperation = ownOperation
            },
            new CashOrder
            {
                Number = "ПКО-2026-9121",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = foreignOperation.Date,
                Amount = 900m,
                Currency = "USD",
                MoneyOperation = foreignOperation
            });
        await db.SaveChangesAsync();
        var controller = new ReportsController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportsController>.Instance)
        {
            ControllerContext = CreateController(db, userId, isAdminClaim: false).ControllerContext,
        };

        var result = await controller.ExportManagementReportCsv("cash", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3),
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, default);

        var csv = System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);
        Assert.Contains("100", csv);
        Assert.DoesNotContain("900", csv);
    }

    [Fact]
    public async Task CashBook_IgnoresOtherCashboxesFutureAndDraftForeignOrders()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9130",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 100m,
                Account = "Своя",
                Currency = " rub "
            },
            new CashOrder
            {
                Number = "ПКО-2026-9131",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 900m,
                Account = "Другая",
                Currency = "USD"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9132",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 4),
                Amount = 900m,
                Account = "Своя",
                Currency = "USD"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9133",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Draft,
                Date = new DateTime(2026, 5, 2),
                Amount = 900m,
                Account = "Своя",
                Currency = "USD"
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        var result = await controller.GetCashBook(new DateTime(2026, 5, 1), new DateTime(2026, 5, 3), "Своя", ct: default);

        var book = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(100m, book.Income);
        Assert.Equal(1, book.IncomingCount);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData(null)]
    public async Task Register_HidesMixedTotalsAcrossPagesAndPrintsEachCurrency(string? currency)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9140",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 20m,
            Currency = currency
        });
        for (var index = 1; index <= 5; index++)
            db.CashOrders.Add(new CashOrder
            {
                Number = $"ПКО-2026-914{index}",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 100m,
                Currency = "RUB"
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";

        var result = await controller.GetRegister(null, null, null, null, null, page: 1, pageSize: 5, ct: default);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(6, register.Total);
        Assert.Equal(5, register.Items.Count);
        Assert.False(register.AmountsAvailable);
        Assert.Null(register.IncomingAmount);
        Assert.Null(register.OutgoingAmount);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(register, new System.Text.Json.JsonSerializerOptions
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("IncomingAmount").ValueKind);
        var printed = Assert.IsType<ContentResult>(await controller.PrintRegister(null, null, null, null, null, ct: default));
        Assert.Contains(currency ?? "валюта не указана", printed.Content);
        Assert.DoesNotContain("520,00 руб.", printed.Content);
    }

    [Fact]
    public async Task Register_UsesPostedOrdersForCurrencyTotalsAndEscapesPrintedCurrency()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9150",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 100m,
                Currency = "RUB"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9151",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Draft,
                Date = new DateTime(2026, 5, 1),
                Amount = 10m,
                Currency = "<tag>"
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";

        var result = await controller.GetRegister(null, null, null, null, null, ct: default);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(register.AmountsAvailable);
        Assert.Equal(100m, register.IncomingAmount);
        var printed = Assert.IsType<ContentResult>(await controller.PrintRegister(null, null, null, null, null, ct: default));
        Assert.Contains("&lt;TAG&gt;", printed.Content);
        Assert.DoesNotContain("<TAG>", printed.Content);
    }

    [Fact]
    public async Task Register_ReportsSingleForeignCurrencyAcrossPages()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: true);
        for (var index = 0; index < 6; index++)
            db.CashOrders.Add(new CashOrder
            {
                Number = $"ПКО-2026-916{index}",
                Type = index == 5 ? CashOrderType.Outgoing : CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 10m,
                Currency = index == 0 ? " usd " : "USD"
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";

        var result = await controller.GetRegister(null, null, null, null, null, page: 1, pageSize: 5, ct: default);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(register.AmountsAvailable);
        Assert.Equal("single", register.CurrencyStatus);
        Assert.Equal("USD", register.Currency);
        Assert.Equal(50m, register.IncomingAmount);
        Assert.Equal(10m, register.OutgoingAmount);
        Assert.Equal(5, register.Items.Count);
        var print = Assert.IsType<ContentResult>(await controller.PrintRegister(null, null, null, null, null, ct: default));
        Assert.Contains("50,00 USD", print.Content);
        Assert.Contains("10,00 USD", print.Content);
        Assert.DoesNotContain("50,00 руб.", print.Content);
    }

    [Fact]
    public async Task Register_EmptyFactHasNoClaimedCurrency()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9169",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Draft,
            Date = new DateTime(2026, 5, 1),
            Amount = 10m,
            Currency = "USD"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var result = await controller.GetRegister(null, null, null, null, null, ct: default);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(register.AmountsAvailable);
        Assert.Equal("empty", register.CurrencyStatus);
        Assert.Null(register.Currency);
        Assert.Equal(0m, register.IncomingAmount);
    }

    [Fact]
    public async Task CashOrderRegister_KeepsStableOrderAcrossPagesWithSameDate()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        var date = new DateTime(2026, 7, 10);
        var orders = Enumerable.Range(1, 12)
            .Select(number => new CashOrder
            {
                Number = $"ПКО-2026-{number:0000}",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = date,
                Amount = number,
            })
            .ToArray();
        db.CashOrders.AddRange(orders);
        await db.SaveChangesAsync();
        var expectedIds = orders.Select(order => order.Id).OrderByDescending(id => id).ToArray();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var actualIds = new List<int>();
        for (var page = 1; page <= 3; page++)
        {
            var action = await controller.GetRegister(
                from: date,
                to: date,
                type: null,
                status: null,
                search: null,
                page: page,
                pageSize: 5,
                accountName: null,
                ct: CancellationToken.None);
            var response = Assert.IsType<CashOrderRegisterResponseDto>(
                Assert.IsType<OkObjectResult>(action.Result).Value);
            actualIds.AddRange(response.Items.Select(item => item.Id));
        }

        Assert.Equal(expectedIds, actualIds);
        Assert.Equal(expectedIds.Length, actualIds.Distinct().Count());
    }

    [Fact]
    public async Task CashOrderLists_UseCompactProjectionAndTwoCashOrderQueries()
    {
        var recorder = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(recorder);
        var userId = await CreateUserWithPaymentsPermissionAsync(
            db,
            canView: true,
            canExport: true);
        var client = new Client
        {
            Name = "Клиент кассового ордера",
            Company = "Служебное полное название клиента",
        };
        var payment = new Payment
        {
            Date = new DateTime(2026, 7, 1),
            PlannedDate = new DateTime(2026, 7, 1),
            Amount = 1_000m,
            PaidAmount = 1_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Description = "Служебное описание связанного платежа",
            Notes = string.Empty,
            SystemNotes = string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 250m,
            Date = new DateTime(2026, 7, 2),
            Purpose = "Служебное назначение денежной операции",
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.Payments.Add(payment);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-1001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 7, 1),
                Amount = payment.Amount,
                ClientId = client.Id,
                PaymentId = payment.Id,
            },
            new CashOrder
            {
                Number = "РКО-2026-1001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 7, 2),
                Amount = operation.Amount,
                MoneyOperationId = operation.Id,
            },
            new CashOrder
            {
                Number = "ПКО-2026-1002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 7, 3),
                Amount = 50m,
                Basis = "INVENTORY correction",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = CreateController(db, userId, isAdminClaim: false);

        recorder.Commands.Clear();
        var pagedAction = await controller.GetPaged(
            from: null,
            to: null,
            type: null,
            status: null,
            search: null,
            page: 1,
            pageSize: 25,
            accountName: null,
            ct: CancellationToken.None);
        var paged = Assert.IsType<PagedResponse<CashOrderDto>>(
            Assert.IsType<OkObjectResult>(pagedAction.Result).Value);
        Assert.Equal(3, paged.Total);
        Assert.Contains(paged.Items, item => item.ClientName == client.Name && item.OriginType == "payment");
        Assert.Contains(paged.Items, item => item.OriginType == "moneyOperation");
        Assert.Contains(paged.Items, item => item.OriginType == "inventory");
        AssertCompactCashOrderCommands(recorder.Commands);

        recorder.Commands.Clear();
        var registerAction = await controller.GetRegister(
            from: null,
            to: null,
            type: null,
            status: null,
            search: null,
            page: 1,
            pageSize: 25,
            accountName: null,
            ct: CancellationToken.None);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(
            Assert.IsType<OkObjectResult>(registerAction.Result).Value);
        Assert.Equal(3, register.Total);
        Assert.Equal(2, register.IncomingCount);
        Assert.Equal(1, register.OutgoingCount);
        AssertCompactCashOrderCommands(recorder.Commands);

        recorder.Commands.Clear();
        var printAction = await controller.PrintRegister(
            from: null,
            to: null,
            type: null,
            status: null,
            search: null,
            accountName: null,
            ct: CancellationToken.None);
        Assert.IsType<ContentResult>(printAction);
        AssertCompactCashOrderCommands(recorder.Commands);
    }

    [Fact]
    public async Task GetNextNumber_ReturnsNextUnusedCashOrderNumber()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 1),
                Amount = 1_000m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 2),
                Amount = 2_000m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var controller = CreateController(db, userId, isAdminClaim: false);
        var action = await controller.GetNextNumber(
            CashOrderType.Incoming,
            new DateTime(2026, 4, 20),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(ok.Value);
        var value = ok.Value;
        var number = value.GetType().GetProperty("number")?.GetValue(value);
        var type = value.GetType().GetProperty("type")?.GetValue(value);
        var date = value.GetType().GetProperty("date")?.GetValue(value);

        Assert.Equal("ПКО-2026-0004", number);
        Assert.Equal(CashOrderType.Incoming, type);
        Assert.Equal(new DateTime(2026, 4, 20), date);
    }

    [Fact]
    public async Task DefaultDates_UseProfileTimeZoneForCashOrderToday()
    {
        await using var db = await CreateContextAsync();
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var userId = await CreateUserWithTimeZoneAsync(db, selectedZone.TimeZoneId);
        await AddConfirmedCashAccountAsync(db, userId, "Main cashbox");
        var controller = CreateController(db, userId);

        var nextNumberAction = await controller.GetNextNumber(
            CashOrderType.Incoming,
            date: null,
            CancellationToken.None);

        var nextNumberOk = Assert.IsType<OkObjectResult>(nextNumberAction);
        Assert.NotNull(nextNumberOk.Value);
        var nextNumberDate = nextNumberOk.Value.GetType().GetProperty("date")?.GetValue(nextNumberOk.Value);
        Assert.Equal(selectedZone.ProfileToday, nextNumberDate);

        var createAction = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = default,
            Amount = 1_200m,
            Currency = "RUB",
            Account = "Main cashbox",
            PayerName = "Client",
            Basis = "Cash payment",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedResult>(createAction);
        var created = Assert.IsType<CashOrderDto>(createdResult.Value);
        Assert.Equal(selectedZone.ProfileToday, created.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", created.Number);

        var cashBookAction = await controller.GetCashBook(
            from: null,
            to: null,
            accountName: "Main cashbox",
            ct: CancellationToken.None);

        var cashBookOk = Assert.IsType<OkObjectResult>(cashBookAction.Result);
        var cashBook = Assert.IsType<CashBookResponseDto>(cashBookOk.Value);
        Assert.Equal(selectedZone.ProfileToday, cashBook.From);
        Assert.Equal(selectedZone.ProfileToday, cashBook.To);
        Assert.Equal(1_200m, cashBook.Income);

        var inventoryAction = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = default,
                AccountName = "Main cashbox",
                ActualBalance = 1_200m,
                CreateCorrection = false,
            },
            CancellationToken.None);

        var inventoryCreated = Assert.IsType<CreatedResult>(inventoryAction);
        var inventory = Assert.IsType<CashInventoryDto>(inventoryCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, inventory.Date);
        Assert.StartsWith($"ИНВК-{selectedZone.ProfileToday.Year}-", inventory.Number);

        var copyAction = await controller.Copy(created.Id, CancellationToken.None);
        var copyCreated = Assert.IsType<CreatedResult>(copyAction);
        var copied = Assert.IsType<CashOrderDto>(copyCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, copied.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", copied.Number);
        Assert.Equal(CashOrderStatus.Draft, copied.Status);
    }

    [Fact]
    public void OutgoingCashOrderPrint_UsesUnifiedKo2Form()
    {
        var method = typeof(CashOrdersController).GetMethod(
            "BuildPrintHtml",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var contextType = typeof(CashOrdersController).GetNestedType(
            "CashOrderPrintContext",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(contextType);
        var context = Activator.CreateInstance(
            contextType!,
            "ООО Тестовая организация",
            "Генеральный директор",
            "Петров П. П.",
            "Иванова И. И.",
            "Сидоров С. С.",
            "Без налога (НДС) 0,00 руб.");

        var html = Assert.IsType<string>(method.Invoke(null, new object[]
        {
            new CashOrder
            {
                Number = "РКО-2026-0042",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 8, 9),
                Amount = 12_345.67m,
                Account = "Основная касса",
                ReceiverName = "Иванов Иван Иванович",
                Basis = "Выдача под отчёт",
                Appendix = "Заявление от 09.08.2026",
            },
            context!,
        }));

        Assert.Contains("Унифицированная форма № КО-2", html);
        Assert.Contains("Форма по ОКУД", html);
        Assert.Contains("0310002", html);
        Assert.Contains("РАСХОДНЫЙ КАССОВЫЙ ОРДЕР", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Иванов Иван Иванович", html);
        Assert.Contains("Выдал кассир", html);
        Assert.Contains("Генеральный директор", html);
        Assert.Contains("Подпись", html);
        Assert.Contains("удостоверяющего личность получателя", html);
        Assert.DoesNotContain("Документ сформирован в PayPlanner", html);
    }

    [Fact]
    public async Task CreateInventory_CalculatesDifferenceAndCreatesCorrectionOrder()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 1_000m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 250m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 500m,
                Account = "Резервная касса",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Основная касса");
        var inventoryCash = await db.MoneyAccounts.SingleAsync();
        foreach (var order in await db.CashOrders.Where(item => item.Account == "Основная касса").ToListAsync())
        {
            order.MoneyAccountId = inventoryCash.Id;
            order.CompanyId = inventoryCash.CompanyId;
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = new DateTime(2026, 5, 3),
                AccountName = " Основная касса ",
                ActualBalance = 800m,
                Comment = "Излишек по пересчету",
                CreateCorrection = true,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var inventory = Assert.IsType<CashInventoryDto>(created.Value);
        Assert.Equal("ИНВК-2026-0001", inventory.Number);
        Assert.Equal(750m, inventory.AccountingBalance);
        Assert.Equal(800m, inventory.ActualBalance);
        Assert.Equal(50m, inventory.Difference);
        Assert.NotNull(inventory.CorrectionCashOrderId);

        var correction = await db.CashOrders.SingleAsync(order => order.Id == inventory.CorrectionCashOrderId);
        Assert.Equal(CashOrderType.Incoming, correction.Type);
        Assert.Equal(CashOrderStatus.Posted, correction.Status);
        Assert.Equal(50m, correction.Amount);
        Assert.Equal("Основная касса", correction.Account);

        var balanceAfterCorrection = await db.CashOrders
            .Where(order => order.Status == CashOrderStatus.Posted && order.Account == "Основная касса")
            .SumAsync(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount);
        Assert.Equal(800m, balanceAfterCorrection);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("USD", true)]
    [InlineData("", true)]
    [InlineData("USD", false)]
    public async Task CreateInventory_RejectsUnconfirmedRubleBalanceWithoutWriting(string? currency, bool createCorrection)
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Account = "Касса",
            Currency = currency,
        });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var confirmedCash = await db.MoneyAccounts.SingleAsync();
        var confirmedOrder = await db.CashOrders.SingleAsync();
        confirmedOrder.MoneyAccountId = confirmedCash.Id;
        confirmedOrder.CompanyId = confirmedCash.CompanyId;
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: true);

        var action = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 200m,
            CreateCorrection = createCorrection,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Equal(1, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CreateInventory_IgnoresOtherAccountsAndFutureOrUnpostedForeignOrders()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9010",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 100m,
                Account = "Касса",
                Currency = " rub "
            },
            new CashOrder
            {
                Number = "ПКО-2026-9011",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 200m,
                Account = "Другая",
                Currency = "USD"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9012",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 4),
                Amount = 200m,
                Account = "Касса",
                Currency = "USD"
            },
            new CashOrder
            {
                Number = "ПКО-2026-9013",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Draft,
                Date = new DateTime(2026, 5, 1),
                Amount = 200m,
                Account = "Касса",
                Currency = "USD"
            });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var inventoryCash = await db.MoneyAccounts.SingleAsync();
        var includedOrder = await db.CashOrders.SingleAsync(item => item.Number == "ПКО-2026-9010");
        includedOrder.MoneyAccountId = inventoryCash.Id;
        includedOrder.CompanyId = inventoryCash.CompanyId;
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: true);
        var action = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 100m,
            CreateCorrection = true,
        }, CancellationToken.None);

        var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(action).Value);
        Assert.Equal(100m, inventory.AccountingBalance);
        Assert.Null(inventory.CorrectionCashOrderId);
        Assert.Equal(4, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CreateInventory_RejectsPartialVisibleBalanceWithoutWriting()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true, viewScope: DataVisibilityScope.Own);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var otherId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-9002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 100m,
                Account = "Касса",
                CreatedByUserId = userId
            },
            new CashOrder
            {
                Number = "ПКО-2026-9003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 50m,
                Account = "Касса",
                CreatedByUserId = otherId
            });
        var confirmedCash = await db.MoneyAccounts.SingleAsync();
        foreach (var order in db.CashOrders.Local)
        {
            order.MoneyAccountId = confirmedCash.Id;
            order.CompanyId = confirmedCash.CompanyId;
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 200m,
            CreateCorrection = true,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Equal(2, await db.CashOrders.CountAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CreateInventory_RejectsCashboxesWithSameNameAcrossCompanies(bool createCorrection, bool isAdmin)
    {
        await using var db = await CreateContextAsync();
        var first = new LegalEntity { ShortName = "Компания 1" };
        var second = new LegalEntity { ShortName = "Компания 2" };
        db.LegalEntities.AddRange(first, second);
        db.MoneyAccounts.AddRange(
            new MoneyAccount { Name = "Общая касса", Source = MoneyOperationSource.Cash, Company = first, IsActive = true },
            new MoneyAccount { Name = "Общая касса", Source = MoneyOperationSource.Cash, Company = second, IsActive = false });
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9020",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Account = "Общая касса"
        });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        if (isAdmin)
        {
            var admin = await db.Users.Include(user => user.Role).SingleAsync(user => user.Id == userId);
            admin.Role!.Name = "admin";
            await db.SaveChangesAsync();
        }
        var controller = CreateController(db, userId, isAdminClaim: isAdmin);

        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Общая касса",
            ActualBalance = 200m,
            CreateCorrection = createCorrection,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Equal(1, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CreateInventory_AllowsOneNamedCashAccount()
    {
        await using var db = await CreateContextAsync();
        db.MoneyAccounts.Add(new MoneyAccount
        {
            Name = "Касса",
            Source = MoneyOperationSource.Cash,
            Company = new LegalEntity { ShortName = "Компания" },
            IsActive = true,
        });
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9030",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Account = "Касса"
        });
        await db.SaveChangesAsync();
        var ownCompanyId = await db.MoneyAccounts.Select(account => account.CompanyId!.Value).SingleAsync();
        var confirmedAccountId = await db.MoneyAccounts.Select(account => account.Id).SingleAsync();
        var confirmedOrder = await db.CashOrders.SingleAsync();
        confirmedOrder.MoneyAccountId = confirmedAccountId;
        confirmedOrder.CompanyId = ownCompanyId;
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { ownCompanyId }) });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var admin = await db.Users.Include(user => user.Role).SingleAsync(user => user.Id == userId);
        admin.Role!.Name = "admin";
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: true);

        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 100m,
            CreateCorrection = false,
        }, CancellationToken.None);

        var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal(100m, inventory.AccountingBalance);
    }

    [Fact]
    public async Task CreateInventory_RejectsKnownCashAccountOutsideUserCompanies()
    {
        await using var db = await CreateContextAsync();
        db.MoneyAccounts.Add(new MoneyAccount
        {
            Name = "Касса",
            Source = MoneyOperationSource.Cash,
            Company = new LegalEntity { ShortName = "Другая компания" },
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 100m,
            CreateCorrection = true,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateInventory_OrdinaryEmployeeChecksLinkedMovementCashAccount(bool matches)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Основная касса");
        var selectedAccount = await db.MoneyAccounts.SingleAsync();
        var otherAccount = new MoneyAccount
        {
            Name = "Другая касса",
            Source = MoneyOperationSource.Cash,
            CompanyId = selectedAccount.CompanyId,
            IsActive = true,
        };
        db.MoneyAccounts.Add(otherAccount);
        await db.SaveChangesAsync();
        var movement = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            MoneyAccountId = matches ? selectedAccount.Id : otherAccount.Id,
            Amount = 50m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            ResponsibleUserId = userId,
        };
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-9400",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = movement.Date,
            Account = selectedAccount.Name,
            Amount = 50m,
            Currency = "RUB",
            CreatedByUserId = userId,
            MoneyOperation = movement,
            MoneyAccountId = selectedAccount.Id,
            CompanyId = selectedAccount.CompanyId,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        var action = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = selectedAccount.Name,
            ActualBalance = 100m,
            CreateCorrection = true,
        }, CancellationToken.None);

        if (matches)
        {
            var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(action).Value);
            Assert.Equal(50m, inventory.AccountingBalance);
            Assert.Equal(50m, inventory.Difference);
            Assert.Equal(2, await db.CashOrders.CountAsync());
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(action);
            Assert.Empty(await db.CashInventories.ToListAsync());
            Assert.Single(await db.CashOrders.ToListAsync());
        }
        var storedMovement = await db.MoneyOperations.SingleAsync();
        Assert.Equal(matches ? selectedAccount.Id : otherAccount.Id, storedMovement.MoneyAccountId);
        Assert.Equal(50m, storedMovement.Amount);
    }

    [Fact]
    public async Task CreateInventory_OrdinaryEmployeeIncludesBoundOrderAfterCashRename()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true, viewScope: DataVisibilityScope.Own);
        await AddConfirmedCashAccountAsync(db, userId, "Новое название");
        var account = await db.MoneyAccounts.SingleAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "SYN-RENAME",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = "RUB",
            Account = "Старое название",
            MoneyAccountId = account.Id,
            CompanyId = account.CompanyId,
            CreatedByUserId = userId
        });
        await db.SaveChangesAsync();
        var result = await CreateController(db, userId, isAdminClaim: false).CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = account.Name,
            MoneyAccountId = account.Id,
            Date = new DateTime(2026, 5, 3),
            ActualBalance = 100m,
            CreateCorrection = true,
        }, CancellationToken.None);
        var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal(100m, inventory.AccountingBalance);
        Assert.Null(inventory.CorrectionCashOrderId);
        Assert.Single(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("payment")]
    [InlineData("movement")]
    [InlineData("invoice")]
    public async Task Update_BoundCashOrderCannotChangeAccountOrOrigin(string changedField)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var cash = await db.MoneyAccounts.SingleAsync();
        var order = new CashOrder
        {
            Number = "SYN-BOUND",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = "RUB",
            Account = cash.Name,
            MoneyAccountId = cash.Id,
            CompanyId = cash.CompanyId,
            CreatedByUserId = userId
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var request = new CashOrder
        {
            Type = order.Type,
            Date = order.Date,
            Amount = order.Amount,
            Currency = order.Currency,
            Account = changedField == "account" ? "Другая касса" : order.Account,
            PaymentId = changedField == "payment" ? 999 : null,
            MoneyOperationId = changedField == "movement" ? 999 : null,
            InvoiceId = changedField == "invoice" ? 999 : null,
            Comment = "Не должно сохраниться"
        };
        var controller = CreateController(db, userId, isAdminClaim: false);
        var result = await controller.Update(order.Id, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        var stored = await db.CashOrders.SingleAsync();
        Assert.Equal(cash.Id, stored.MoneyAccountId);
        Assert.Equal(cash.CompanyId, stored.CompanyId);
        Assert.Equal("Касса", stored.Account);
        Assert.Null(stored.PaymentId);
        Assert.Null(stored.MoneyOperationId);
        Assert.Null(stored.InvoiceId);
        Assert.Null(stored.Comment);
    }

    [Fact]
    public async Task Update_BoundCashOrderAllowsDescriptionWithoutChangingIdentity()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var cash = await db.MoneyAccounts.SingleAsync();
        var order = new CashOrder
        {
            Number = "SYN-BOUND-EDIT",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = "RUB",
            Account = cash.Name,
            MoneyAccountId = cash.Id,
            CompanyId = cash.CompanyId,
            CreatedByUserId = userId
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var result = await CreateController(db, userId, isAdminClaim: false).Update(order.Id, new CashOrder
        {
            Type = order.Type,
            Date = order.Date,
            Amount = order.Amount,
            Currency = order.Currency,
            Account = " Касса ",
            Comment = "Уточнение",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var stored = await db.CashOrders.SingleAsync();
        Assert.Equal(cash.Id, stored.MoneyAccountId);
        Assert.Equal(cash.CompanyId, stored.CompanyId);
        Assert.Equal("Уточнение", stored.Comment);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetById_BoundCashOrderRequiresAccessToItsCompany(bool ownCompany)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var own = await db.MoneyAccounts.SingleAsync();
        var other = new MoneyAccount
        {
            Name = "Другая касса",
            Source = MoneyOperationSource.Cash,
            Company = new LegalEntity { ShortName = "Другая компания" },
            IsActive = true
        };
        db.MoneyAccounts.Add(other);
        await db.SaveChangesAsync();
        var setting = await db.SystemSettings.SingleAsync(item => item.Key == "legalEntities.mainCompanyIds");
        setting.Value = System.Text.Json.JsonSerializer.Serialize(new[] { own.CompanyId, other.CompanyId });
        var source = ownCompany ? own : other;
        var order = new CashOrder
        {
            Number = "SYN-BOUND-READ",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = "RUB",
            Account = source.Name,
            MoneyAccountId = source.Id,
            CompanyId = source.CompanyId,
            CreatedByUserId = userId
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        var result = await controller.GetById(order.Id, CancellationToken.None);
        if (ownCompany) Assert.IsType<OkObjectResult>(result);
        else Assert.IsType<NotFoundResult>(result);
        var registryResult = await controller.GetRegister(null, null, null, null, null, ct: CancellationToken.None);
        var registry = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(registryResult.Result).Value);
        Assert.Equal(ownCompany ? 1 : 0, registry.Total);
        Assert.Equal(ownCompany ? 1 : 0, registry.IncomingCount);
        Assert.Equal(ownCompany ? 100m : 0m, registry.IncomingAmount ?? 0m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateFromMoneyOperation_RejectsUnknownCurrencyWithoutWriting(string? currency)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = currency
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        var result = await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashOrders.ToListAsync());
        Assert.Equal(currency, (await db.MoneyOperations.SingleAsync()).Currency);
    }

    [Theory]
    [InlineData("RUB")]
    [InlineData("USD")]
    public async Task CreateFromMoneyOperation_PreservesKnownCurrencyAndReturnsExistingOrder(string currency)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = currency
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var created = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.CreateFromMoneyOperation(operation.Id, default)).Value);
        var repeated = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(await controller.CreateFromMoneyOperation(operation.Id, default)).Value);
        Assert.Equal(currency, created.Currency);
        Assert.Equal(created.Id, repeated.Id);
        Assert.Equal(1, await db.CashOrders.CountAsync());
        var historical = await db.CashOrders.SingleAsync();
        Assert.Null(historical.MoneyAccountId);
        Assert.Null(historical.CompanyId);
    }

    [Fact]
    public async Task CreateFromMoneyOperation_PersistsConfirmedCashIdentityAndKeepsItOnRetry()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = account.Id,
            AccountName = account.Name,
            Amount = 100m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            ResponsibleUserId = userId
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        Assert.IsType<CreatedResult>(await controller.CreateFromMoneyOperation(operation.Id, CancellationToken.None));
        var order = await db.CashOrders.SingleAsync();
        Assert.Equal(account.Id, order.MoneyAccountId);
        Assert.Equal(account.CompanyId, order.CompanyId);
        Assert.IsType<OkObjectResult>(await controller.CreateFromMoneyOperation(operation.Id, CancellationToken.None));
        Assert.Equal(1, await db.CashOrders.CountAsync());
        Assert.Equal(account.Id, (await db.CashOrders.SingleAsync()).MoneyAccountId);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("operation")]
    [InlineData("rights")]
    public async Task CreateFromMoneyOperation_RejectsStaleAccountOperationOrRightsBeforeWriting(string target)
    {
        var interceptor = new CashOrderStaleLockInterceptor(target);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = account.Id,
            AccountName = account.Name,
            Amount = 100m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            ResponsibleUserId = userId
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        interceptor.Enabled = true;
        var result = await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        if (target == "rights") Assert.IsType<ForbidResult>(result);
        else Assert.IsType<ConflictObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.CashOrders.ToListAsync());
        Assert.True((await db.MoneyAccounts.SingleAsync()).IsActive);
        Assert.Equal(100m, (await db.MoneyOperations.SingleAsync()).Amount);
        Assert.True((await db.RolePermissions.SingleAsync()).CanCreate);
    }

    [Fact]
    public async Task CreateFromMoneyOperation_ConcurrentRequestsDoNotCreateDuplicateOrders()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"synthetic-cash-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var seedOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(anchor).Options;
        await using var seed = new PaymentContext(seedOptions);
        await seed.Database.EnsureCreatedAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(seed, canView: true, canCreate: true);
        await AddConfirmedCashAccountAsync(seed, userId, "Касса");
        var account = await seed.MoneyAccounts.SingleAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = account.Id,
            AccountName = account.Name,
            Amount = 100m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            ResponsibleUserId = userId
        };
        seed.MoneyOperations.Add(operation);
        await seed.SaveChangesAsync();

        var gate = new CashOrderConcurrentReadGate();
        async Task<IActionResult?> InvokeAsync()
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection)
                .AddInterceptors(new CashOrderConcurrentReadInterceptor(gate)).Options;
            await using var db = new PaymentContext(options);
            try { return await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, CancellationToken.None); }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { return null; }
        }
        var results = await Task.WhenAll(Task.Run(InvokeAsync), Task.Run(InvokeAsync));
        Assert.Equal(2, gate.Arrivals);
        Assert.Contains(results, result => result is CreatedResult);
        Assert.All(results, result => Assert.True(result is null or CreatedResult or OkObjectResult));
        seed.ChangeTracker.Clear();
        var order = Assert.Single(await seed.CashOrders.ToListAsync());
        Assert.Equal(operation.Id, order.MoneyOperationId);
        Assert.Equal(account.Id, order.MoneyAccountId);
        Assert.IsType<OkObjectResult>(await CreateController(seed, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, CancellationToken.None));
        Assert.Single(await seed.CashOrders.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentInventories_DoNotCreateDuplicateCorrectionForSameBalance()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"synthetic-inventory-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var seed = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(anchor).Options);
        await seed.Database.EnsureCreatedAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(seed, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(seed, userId, "Касса");
        var account = await seed.MoneyAccounts.SingleAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<IActionResult> InvokeAsync()
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
            if (Interlocked.Increment(ref arrivals) == 2) start.TrySetResult();
            await start.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return await CreateController(db, userId, isAdminClaim: false).CreateInventory(new CashInventoryCreateRequest
            {
                AccountName = account.Name,
                MoneyAccountId = account.Id,
                Date = new DateTime(2026, 10, 28),
                ActualBalance = 100m,
                CreateCorrection = true,
            }, CancellationToken.None);
        }
        var first = Task.Run(InvokeAsync);
        var second = Task.Run(InvokeAsync);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(2, arrivals);
        Assert.Contains(results, result => result is CreatedResult);
        Assert.All(results, result => Assert.True(result is ConflictObjectResult or CreatedResult));
        seed.ChangeTracker.Clear();
        var adjustment = Assert.Single(await seed.CashOrders.ToListAsync());
        Assert.Equal(100m, adjustment.Amount);
        Assert.Equal(account.Id, adjustment.MoneyAccountId);
        var inventories = await seed.CashInventories.ToListAsync();
        Assert.InRange(inventories.Count, 1, 2);
        Assert.Single(inventories, item => item.CorrectionCashOrderId == adjustment.Id);
        Assert.All(inventories.Where(item => item.CorrectionCashOrderId == null), item => Assert.Equal(0m, item.Difference));
        if (results.Any(result => result is ConflictObjectResult))
        {
            var retry = await CreateController(seed, userId, isAdminClaim: false).CreateInventory(new CashInventoryCreateRequest
            {
                AccountName = account.Name,
                MoneyAccountId = account.Id,
                Date = new DateTime(2026, 10, 28),
                ActualBalance = 100m,
                CreateCorrection = true,
            }, CancellationToken.None);
            var retryDto = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(retry).Value);
            Assert.Equal(0m, retryDto.Difference);
            Assert.Null(retryDto.CorrectionCashOrderId);
            Assert.Single(await seed.CashOrders.ToListAsync());
        }
    }

    [Theory]
    [InlineData("account")]
    [InlineData("permission")]
    [InlineData("company-access")]
    public async Task CreateInventory_RejectsChangesBeforeCommit(string target)
    {
        var interceptor = new InventoryStaleLockInterceptor(target);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        interceptor.Enabled = true;
        var result = await CreateController(db, userId, isAdminClaim: false).CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = account.Name,
            MoneyAccountId = account.Id,
            Date = new DateTime(2026, 10, 28),
            ActualBalance = 100m,
            CreateCorrection = true,
        }, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        if (target == "account") Assert.IsType<ConflictObjectResult>(result);
        else Assert.IsType<ForbidResult>(result);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
        Assert.True((await db.MoneyAccounts.SingleAsync()).IsActive);
        Assert.True((await db.RolePermissions.SingleAsync()).CanEdit);
        Assert.Single(await db.UserCompanyAccesses.ToListAsync());
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task CreateInventory_ReportsTransientDatabaseConflictWithoutWriting(int code, bool wrapped)
    {
        var interceptor = new InventoryTransientFailureInterceptor(code, wrapped);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        interceptor.Enabled = true;
        var controller = CreateController(db, userId, isAdminClaim: false);
        var request = new CashInventoryCreateRequest
        {
            AccountName = account.Name,
            MoneyAccountId = account.Id,
            Date = new DateTime(2026, 10, 28),
            ActualBalance = 100m,
            CreateCorrection = true
        };
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.CreateInventory(request, CancellationToken.None));
        Assert.Contains("Обновите", Assert.IsType<string>(conflict.Value));
        Assert.True(interceptor.Triggered);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.CashOrders.ToListAsync());
        Assert.Empty(await db.CashInventories.ToListAsync());
        interceptor.Enabled = false;
        Assert.IsType<CreatedResult>(await controller.CreateInventory(request, CancellationToken.None));
        Assert.Single(await db.CashOrders.ToListAsync());
        Assert.Single(await db.CashInventories.ToListAsync());
    }

    [Fact]
    public async Task CreateInventory_DoesNotHideUnrelatedDatabaseErrorsAsConcurrency()
    {
        var interceptor = new InventoryTransientFailureInterceptor(19, false);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<SqliteException>(() => CreateController(db, userId, isAdminClaim: false).CreateInventory(
            new CashInventoryCreateRequest { AccountName = account.Name, MoneyAccountId = account.Id, ActualBalance = 100m }, CancellationToken.None));
        Assert.Empty(await db.CashInventories.ToListAsync());
    }

    [Fact]
    public async Task CreateInventory_LeavesAmbientTransactionFailureToItsOwner()
    {
        var interceptor = new InventoryTransientFailureInterceptor(5, false);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<SqliteException>(() => CreateController(db, userId, isAdminClaim: false).CreateInventory(
            new CashInventoryCreateRequest { AccountName = account.Name, MoneyAccountId = account.Id, ActualBalance = 100m }, CancellationToken.None));
        await transaction.RollbackAsync();
        Assert.Empty(await db.CashInventories.ToListAsync());
    }

    [Theory]
    [InlineData("40001", true)]
    [InlineData("40P01", true)]
    [InlineData("55P03", true)]
    [InlineData("23505", false)]
    public async Task CreateInventory_ClassifiesPostgresErrorsWithoutHidingOtherFailures(string sqlState, bool conflict)
    {
        var interceptor = new InventoryPostgresFailureInterceptor(sqlState);
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        interceptor.Enabled = true;
        var controller = CreateController(db, userId, isAdminClaim: false);
        var request = new CashInventoryCreateRequest { AccountName = account.Name, MoneyAccountId = account.Id, ActualBalance = 100m };
        if (conflict) Assert.IsType<ConflictObjectResult>(await controller.CreateInventory(request, CancellationToken.None));
        else await Assert.ThrowsAsync<Npgsql.PostgresException>(() => controller.CreateInventory(request, CancellationToken.None));
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("inactive")]
    [InlineData("source")]
    public async Task CreateFromMoneyOperation_DoesNotBindUnusableCashAccount(string invalid)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        if (invalid == "inactive") account.IsActive = false;
        if (invalid == "source") account.Source = MoneyOperationSource.Bank;
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = account.Id,
            AccountName = account.Name,
            Amount = 100m,
            Currency = invalid == "currency" ? "USD" : "RUB",
            Date = new DateTime(2026, 5, 1),
            ResponsibleUserId = userId
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var result = await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashOrders.ToListAsync());
        Assert.Equal(account.Id, (await db.MoneyOperations.SingleAsync()).MoneyAccountId);
    }

    [Fact]
    public async Task CreateFromMoneyOperation_ReusesHistoricalOrderWhenCurrencyIsUnknown()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = null
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var order = new CashOrder
        {
            Number = "ПКО-2026-9040",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = operation.Date,
            Amount = operation.Amount,
            MoneyOperationId = operation.Id,
            Currency = "RUB"
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();

        var result = await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, default);

        var returned = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(order.Id, returned.Id);
        Assert.Equal("RUB", returned.Currency);
        Assert.Null((await db.MoneyOperations.SingleAsync()).Currency);
        Assert.Equal(1, await db.CashOrders.CountAsync());
    }

    [Fact]
    public async Task CreateFromMoneyOperation_DoesNotExposeAnotherCompanyOperation()
    {
        await using var db = await CreateContextAsync();
        var own = new LegalEntity { ShortName = "Своя" };
        var foreign = new LegalEntity { ShortName = "Другая" };
        db.LegalEntities.AddRange(own, foreign);
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.IsEmployee = true;
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = own.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) });
        var account = new MoneyAccount { Name = "Другая касса", Source = MoneyOperationSource.Cash, CompanyId = foreign.Id };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = null,
            MoneyAccount = account
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        var result = await CreateController(db, userId, isAdminClaim: false).CreateFromMoneyOperation(operation.Id, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Copy_UnknownCurrencyRemainsUnknownAndCannotBePosted(string? currency)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var source = new CashOrder
        {
            Number = "ПКО-2026-9050",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = currency
        };
        db.CashOrders.Add(source);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var copy = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.Copy(source.Id, default)).Value);
        Assert.True(string.IsNullOrWhiteSpace(copy.Currency));
        Assert.IsType<BadRequestObjectResult>(await controller.Post(copy.Id, default));
        var stored = await db.CashOrders.SingleAsync(order => order.Id == copy.Id);
        Assert.Equal(CashOrderStatus.Draft, stored.Status);
        Assert.True(string.IsNullOrWhiteSpace(stored.Currency));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Update_UnknownCurrencyDoesNotBecomeRubles(string? currency)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var order = new CashOrder
        {
            Number = "ПКО-2026-9051",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Draft,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = currency
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();

        var result = await CreateController(db, userId, isAdminClaim: false).Update(order.Id,
            new CashOrder { Type = order.Type, Date = order.Date, Amount = order.Amount, Currency = currency, Comment = "Описание" }, default);

        var saved = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(string.IsNullOrWhiteSpace(saved.Currency));
        Assert.Equal("Описание", saved.Comment);
        Assert.True(string.IsNullOrWhiteSpace((await db.CashOrders.SingleAsync()).Currency));
    }

    [Fact]
    public async Task Copy_KnownCurrencyCanBePosted()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var source = new CashOrder
        {
            Number = "ПКО-2026-9052",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = "USD"
        };
        db.CashOrders.Add(source);
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var copy = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.Copy(source.Id, default)).Value);
        var posted = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(await controller.Post(copy.Id, default)).Value);
        Assert.Equal("USD", posted.Currency);
        Assert.Equal(CashOrderStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task Post_PreviouslyPostedUnknownCurrencyIsIdempotent()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var order = new CashOrder
        {
            Number = "ПКО-2026-9053",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 100m,
            Currency = null
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();

        var result = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(
            await CreateController(db, userId, isAdminClaim: false).Post(order.Id, default)).Value);
        Assert.Null(result.Currency);
        Assert.Equal(CashOrderStatus.Posted, result.Status);
        Assert.Equal(1, await db.CashOrders.CountAsync());
    }

    [Theory]
    [InlineData("{\"type\":0,\"status\":1,\"amount\":100}")]
    [InlineData("{\"type\":0,\"status\":1,\"amount\":100,\"currency\":null}")]
    [InlineData("{\"type\":0,\"status\":1,\"amount\":100,\"currency\":\"  \"}")]
    public async Task Create_PostedOrderWithUnknownJsonCurrencyIsRejected(string json)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var model = System.Text.Json.JsonSerializer.Deserialize<CashOrder>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var result = await CreateController(db, userId, isAdminClaim: false).Create(model, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Fact]
    public async Task Create_DraftWithOmittedJsonCurrencyRemainsUnknownUntilSpecified()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true, canEdit: true);
        var model = System.Text.Json.JsonSerializer.Deserialize<CashOrder>("{\"type\":0,\"status\":0,\"amount\":100}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var controller = CreateController(db, userId, isAdminClaim: false);

        var created = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(await controller.Create(model, default)).Value);
        Assert.Null(created.Currency);
        Assert.IsType<BadRequestObjectResult>(await controller.Post(created.Id, default));
        Assert.Null((await db.CashOrders.SingleAsync()).Currency);
        var update = await controller.Update(created.Id, new CashOrder
        {
            Type = created.Type,
            Date = created.Date,
            Amount = created.Amount,
            Currency = "RUB",
        }, default);
        Assert.Equal("RUB", Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(update).Value).Currency);
        var posted = Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(await controller.Post(created.Id, default)).Value);
        Assert.Equal(CashOrderStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task Create_PostedOrderWithExplicitCurrencyPreservesIt()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: true);
        var model = System.Text.Json.JsonSerializer.Deserialize<CashOrder>("{\"type\":0,\"status\":1,\"amount\":100,\"currency\":\"USD\"}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var result = await CreateController(db, userId, isAdminClaim: false).Create(model, default);

        var created = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(CashOrderStatus.Posted, created.Status);
    }

    [Fact]
    public async Task CreateInventory_AllowsActiveCashAccountOfAssignedCompany()
    {
        await using var db = await CreateContextAsync();
        var company = new LegalEntity { ShortName = "Своя компания" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true);
        var employee = await db.Users.SingleAsync(user => user.Id == userId);
        employee.IsEmployee = true;
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = company.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        db.MoneyAccounts.Add(new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, CompanyId = company.Id, IsActive = true });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            Date = new DateTime(2026, 5, 3),
            AccountName = "Касса",
            ActualBalance = 0m,
            CreateCorrection = false,
        }, CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
    }

    [Fact]
    public async Task GetInventories_FiltersByAccountAndPeriod()
    {
        await using var db = await CreateContextAsync();
        db.CashInventories.AddRange(
            new CashInventory
            {
                Number = "ИНВК-2026-0001",
                Date = new DateTime(2026, 5, 1),
                AccountName = "Основная касса",
                AccountingBalance = 1_000m,
                ActualBalance = 1_000m,
                Difference = 0m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashInventory
            {
                Number = "ИНВК-2026-0002",
                Date = new DateTime(2026, 5, 2),
                AccountName = "Резервная касса",
                AccountingBalance = 500m,
                ActualBalance = 450m,
                Difference = -50m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        var admin = await db.Users.Include(user => user.Role).SingleAsync(user => user.Id == userId);
        admin.Role!.Name = "admin";
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: true);

        var action = await controller.GetInventories(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            accountName: "Основная касса",
            page: 1,
            pageSize: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<CashInventoryPageResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal("ИНВК-2026-0001", item.Number);
        Assert.Equal("Основная касса", item.AccountName);
        Assert.Equal(1, response.Total);
    }

    [Theory]
    [InlineData(DataVisibilityScope.Own)]
    [InlineData(DataVisibilityScope.All)]
    public async Task GetInventories_ShowsOnlyConfirmedCompanyAndVisibleCreator(string viewScope)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: viewScope);
        var otherUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        await AddConfirmedCashAccountAsync(db, userId, "Собственная касса");
        var own = await db.MoneyAccounts.SingleAsync();
        var otherCompany = new LegalEntity { ShortName = "Чужая компания" };
        var foreign = new MoneyAccount { Name = "Чужая касса", Source = MoneyOperationSource.Cash, Company = otherCompany, IsActive = true };
        db.MoneyAccounts.Add(foreign);
        await db.SaveChangesAsync();
        var setting = await db.SystemSettings.SingleAsync(item => item.Key == "legalEntities.mainCompanyIds");
        setting.Value = System.Text.Json.JsonSerializer.Serialize(new[] { own.CompanyId, foreign.CompanyId });
        var ownRow = new CashInventory { Number = "SYN-OWN", AccountName = own.Name, Date = new DateTime(2026, 5, 1), CompanyId = own.CompanyId, MoneyAccountId = own.Id };
        var colleagueRow = new CashInventory { Number = "SYN-COLLEAGUE", AccountName = own.Name, Date = new DateTime(2026, 5, 1), CompanyId = own.CompanyId, MoneyAccountId = own.Id };
        var foreignRow = new CashInventory { Number = "SYN-FOREIGN", AccountName = foreign.Name, Date = new DateTime(2026, 5, 1), CompanyId = foreign.CompanyId, MoneyAccountId = foreign.Id };
        var legacy = new CashInventory { Number = "SYN-LEGACY", AccountName = own.Name, Date = new DateTime(2026, 5, 1) };
        var inconsistent = new CashInventory { Number = "SYN-INCONSISTENT", AccountName = own.Name, Date = new DateTime(2026, 5, 1), CompanyId = foreign.CompanyId, MoneyAccountId = own.Id };
        db.CashInventories.AddRange(ownRow, colleagueRow, foreignRow, legacy, inconsistent);
        db.Entry(ownRow).Property<int?>("CreatedByUserId").CurrentValue = userId;
        db.Entry(colleagueRow).Property<int?>("CreatedByUserId").CurrentValue = otherUserId;
        db.Entry(foreignRow).Property<int?>("CreatedByUserId").CurrentValue = userId;
        db.Entry(legacy).Property<int?>("CreatedByUserId").CurrentValue = userId;
        db.Entry(inconsistent).Property<int?>("CreatedByUserId").CurrentValue = userId;
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var result = await controller.GetInventories(null, null, ct: CancellationToken.None);
        var page = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(viewScope == DataVisibilityScope.All ? 2 : 1, page.Total);
        Assert.Contains(page.Items, item => item.Number == "SYN-OWN");
        Assert.Equal(viewScope == DataVisibilityScope.All, page.Items.Any(item => item.Number == "SYN-COLLEAGUE"));
        Assert.DoesNotContain(page.Items, item => item.Number is "SYN-FOREIGN" or "SYN-LEGACY" or "SYN-INCONSISTENT");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetInventories_OrdinaryCreatorCanSeeNewInventory(bool createCorrection)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: true, viewScope: DataVisibilityScope.Own);
        await AddConfirmedCashAccountAsync(db, userId, "Собственная касса");
        var account = await db.MoneyAccounts.SingleAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);
        Assert.IsType<CreatedResult>(await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = account.Name,
            MoneyAccountId = account.Id,
            Date = new DateTime(2026, 5, 1),
            ActualBalance = 100m,
            CreateCorrection = createCorrection,
        }, CancellationToken.None));

        var result = await controller.GetInventories(null, null, ct: CancellationToken.None);
        var page = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        var inventory = Assert.Single(page.Items);
        Assert.Equal(100m, inventory.ActualBalance);
        Assert.Equal(createCorrection, inventory.CorrectionCashOrderId.HasValue);
        db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync(item => item.UserId == userId));
        await db.SaveChangesAsync();
        var afterRevocation = await controller.GetInventories(null, null, ct: CancellationToken.None);
        var filtered = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(afterRevocation.Result).Value);
        Assert.Equal(0, filtered.Total);
        Assert.Empty(filtered.Items);
    }

    [Fact]
    public async Task GetInventories_HidesInaccessibleCorrectionEvenWhenInventoryBelongsToUser()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var otherUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        await AddConfirmedCashAccountAsync(db, userId, "Собственная касса");
        var account = await db.MoneyAccounts.SingleAsync();
        var correction = new CashOrder { Number = "SYN-HIDDEN", Account = account.Name, Date = new DateTime(2026, 5, 1), CreatedByUserId = otherUserId };
        var inventory = new CashInventory
        {
            Number = "SYN-INVENTORY",
            AccountName = account.Name,
            Date = correction.Date,
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            CorrectionCashOrder = correction
        };
        db.CashInventories.Add(inventory);
        db.Entry(inventory).Property<int?>("CreatedByUserId").CurrentValue = userId;
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var result = await controller.GetInventories(null, null, ct: CancellationToken.None);
        var page = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData(DataVisibilityScope.Own)]
    [InlineData(DataVisibilityScope.All)]
    public async Task GetInventories_WithoutReliableCompanyOwnership_HidesLegacyFromNonAdmin(string viewScope)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: viewScope);
        db.CashInventories.Add(new CashInventory
        {
            Number = "ИНВК-2026-0091",
            Date = new DateTime(2026, 5, 1),
            AccountName = "Касса",
            AccountingBalance = 100m,
            ActualBalance = 100m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var result = await controller.GetInventories(null, null, ct: CancellationToken.None);

        var page = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetInventories_AdminClaimDoesNotOverrideCurrentDatabaseRole()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        db.CashInventories.Add(new CashInventory { Number = "SYN-LEGACY", AccountName = "Касса", Date = new DateTime(2026, 5, 1) });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId, isAdminClaim: true);

        var result = await controller.GetInventories(null, null, ct: CancellationToken.None);

        var page = Assert.IsType<CashInventoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task CashBook_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetCashBook(null, null, null, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task Create_WithoutPaymentsCreatePermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task PrintRegister_WithoutPaymentsExportPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canExport: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.PrintRegister(null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Register_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetRegister(null, null, null, null, null, 1, 25, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task GetById_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetById(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task RegisterAndCashBook_ApplyPaymentsVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var visibleClient = new Client { Name = "Visible cash register client", ResponsibleUserId = userId };
        var hiddenClient = new Client { Name = "Hidden cash register client", ResponsibleUserId = hiddenUserId };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "PKO-VISIBLE",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 1_000m,
                ClientId = visibleClient.Id,
                Account = "Основная касса",
            },
            new CashOrder
            {
                Number = "PKO-HIDDEN",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 7_000m,
                ClientId = hiddenClient.Id,
                Account = "Основная касса",
            },
            new CashOrder
            {
                Number = "RKO-OWN",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 300m,
                CreatedByUserId = userId,
                Account = "Основная касса",
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var registerAction = await controller.GetRegister(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            null,
            1,
            25,
            "Основная касса",
            CancellationToken.None);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(Assert.IsType<OkObjectResult>(registerAction.Result).Value);
        Assert.Equal(2, register.Total);
        Assert.Equal(1_000m, register.IncomingAmount);
        Assert.Equal(300m, register.OutgoingAmount);
        Assert.DoesNotContain(register.Items, item => item.Number == "PKO-HIDDEN");

        var cashBookAction = await controller.GetCashBook(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 2),
            "Основная касса",
            ct: CancellationToken.None);
        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(cashBookAction.Result).Value);
        Assert.Equal(1_000m, cashBook.Income);
        Assert.Equal(300m, cashBook.Expense);
        Assert.Equal(700m, cashBook.ClosingBalance);
    }

    [Fact]
    public async Task CashBook_AppliesLinkedPaymentFilters()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);

        var targetClient = new Client { Name = "Target Client" };
        var otherClient = new Client { Name = "Other Client" };
        var targetCase = new ClientCase { CaseNumber = "CASE-1", Client = targetClient };
        var otherCase = new ClientCase { CaseNumber = "CASE-2", Client = otherClient };
        var targetContract = new Contract { Number = "CONTRACT-1", Date = new DateTime(2026, 5, 1) };
        var otherContract = new Contract { Number = "CONTRACT-2", Date = new DateTime(2026, 5, 1) };
        var targetIncomeType = new IncomeType { Name = "Target income", PaymentType = PaymentType.Income };
        var otherIncomeType = new IncomeType { Name = "Other income", PaymentType = PaymentType.Income };
        db.AddRange(targetClient, otherClient, targetCase, otherCase, targetContract, otherContract, targetIncomeType, otherIncomeType);
        await db.SaveChangesAsync();

        var targetPayment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Amount = 1_500m,
            PaidAmount = 1_500m,
            Description = "Target cash payment",
            CounterpartyName = "Target payer",
            ClientId = targetClient.Id,
            ClientCaseId = targetCase.Id,
            ContractId = targetContract.Id,
            IncomeTypeId = targetIncomeType.Id,
        };
        var otherPayment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Amount = 9_000m,
            PaidAmount = 9_000m,
            Description = "Other cash payment",
            CounterpartyName = "Other payer",
            ClientId = otherClient.Id,
            ClientCaseId = otherCase.Id,
            ContractId = otherContract.Id,
            IncomeTypeId = otherIncomeType.Id,
        };
        db.Payments.AddRange(targetPayment, otherPayment);
        await db.SaveChangesAsync();

        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "PKO-FILTER-TARGET",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 10),
                Amount = 1_500m,
                Account = "Main cashbox",
                PayerName = "Target payer",
                ClientId = targetClient.Id,
                PaymentId = targetPayment.Id,
            },
            new CashOrder
            {
                Number = "PKO-FILTER-OTHER",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 10),
                Amount = 9_000m,
                Account = "Main cashbox",
                PayerName = "Other payer",
                ClientId = otherClient.Id,
                PaymentId = otherPayment.Id,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetCashBook(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            "Main cashbox",
            counterpartyName: "Target",
            clientId: targetClient.Id,
            caseId: targetCase.Id,
            contractId: targetContract.Id,
            incomeTypeId: targetIncomeType.Id,
            ct: CancellationToken.None);

        var cashBook = Assert.IsType<CashBookResponseDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1_500m, cashBook.Income);
        Assert.Equal(0m, cashBook.Expense);
        Assert.Equal(1_500m, cashBook.ClosingBalance);
        Assert.Equal(1, cashBook.IncomingCount);
    }

    [Fact]
    public async Task GetById_HiddenCashOrder_ReturnsNotFoundForLimitedRole()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenUserId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, viewScope: DataVisibilityScope.Own);
        var hiddenClient = new Client { Name = "Hidden cash order client", ResponsibleUserId = hiddenUserId };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();
        var hiddenOrder = new CashOrder
        {
            Number = "PKO-HIDDEN-ID",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 7_000m,
            ClientId = hiddenClient.Id,
        };
        db.CashOrders.Add(hiddenOrder);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetById(hiddenOrder.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task Update_WithoutPaymentsEditPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Update(1, new CashOrder
        {
            Type = CashOrderType.Incoming,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Delete_WithoutPaymentsDeletePermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canDelete: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Delete(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task PrintForPayment_UsesOfficialIncomingFormWithoutCreatingCashOrder()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        var mainCompany = new LegalEntity
        {
            ShortName = "ИП Тестовый",
            VatWithoutVat = false,
        };
        var client = new Client { Name = "Игнатьев Борис Никифорович" };
        db.LegalEntities.Add(mainCompany);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = mainCompany.Id.ToString(),
        });
        var payment = new Payment
        {
            Date = new DateTime(2026, 8, 28),
            PlannedDate = new DateTime(2026, 8, 28),
            Amount = 5_000m,
            PaidAmount = 0m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Оплата по счету №1556",
            ClientId = client.Id,
            Client = client,
            Account = "1556",
            CreatedAt = new DateTime(2026, 8, 27, 8, 0, 0, DateTimeKind.Utc),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.PrintForPayment(payment.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Contains("Унифицированная форма № КО-1", content.Content);
        Assert.Contains("Приходный кассовый ордер", content.Content);
        Assert.Contains("Игнатьев Борис Никифорович", content.Content);
        Assert.Contains("Оплата по счету №1556", content.Content);
        Assert.Contains($">{payment.Id}<", content.Content);
        Assert.Contains("Без налога (НДС) 0,00 руб.", content.Content);
        Assert.DoesNotContain("НДС 833,33 руб.", content.Content);
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Fact]
    public async Task PrintForPayment_UsesExistingLinkedCashOrder()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        var mainCompany = new LegalEntity
        {
            ShortName = "ИП Тестовый",
            VatWithoutVat = false,
        };
        db.LegalEntities.Add(mainCompany);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = mainCompany.Id.ToString(),
        });
        var payment = new Payment
        {
            Date = new DateTime(2026, 8, 28),
            PlannedDate = new DateTime(2026, 8, 28),
            Amount = 5_000m,
            PaidAmount = 5_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Description = "Оплата наличными",
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 5_000m,
            VatRate = 20m,
            VatAmount = 833.33m,
            Date = payment.Date,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0042",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = payment.Date,
            Amount = payment.PaidAmount,
            PaymentId = payment.Id,
            MoneyOperationId = operation.Id,
            PayerName = "Плательщик из ордера",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.PrintForPayment(payment.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Contains("ПКО-2026-0042", content.Content);
        Assert.Contains("Плательщик из ордера", content.Content);
        Assert.Contains("НДС 833,33 руб.", content.Content);
    }

    [Fact]
    public async Task Print_WithoutPaymentsViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.Print(1, CancellationToken.None);
        var paymentAction = await controller.PrintForPayment(1, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
        Assert.IsType<ForbidResult>(paymentAction);
    }

    [Fact]
    public async Task CreateInventory_WithoutPaymentsEditPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: false);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = new DateTime(2026, 5, 1),
                AccountName = "Main cashbox",
                ActualBalance = 1_000m,
                CreateCorrection = false,
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task GetById_IncludesOriginAndAuditFields()
    {
        await using var db = await CreateContextAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 1),
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        var order = new CashOrder
        {
            Number = "PKO-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 5, 1),
            Amount = 1_000m,
            MoneyOperationId = operation.Id,
            CreatedByUserId = 7,
            CreatedByUserName = "Бухгалтер",
            CreatedAt = DateTime.UtcNow,
        };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true);
        var controller = CreateController(db, userId, isAdminClaim: false);

        var action = await controller.GetById(order.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var dto = Assert.IsType<CashOrderDto>(ok.Value);
        Assert.Equal("moneyOperation", dto.OriginType);
        Assert.Equal(7, dto.CreatedByUserId);
        Assert.Equal("Бухгалтер", dto.CreatedByUserName);
    }

    private static void AssertCompactCashOrderCommands(IReadOnlyCollection<string> commands)
    {
        var cashOrderCommands = commands
            .Where(sql => sql.Contains("FROM \"CashOrders\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, cashOrderCommands.Length);

        var itemsCommand = Assert.Single(cashOrderCommands, sql => sql.Contains("ORDER BY", StringComparison.Ordinal));
        Assert.DoesNotContain("JOIN \"Payments\"", itemsCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN \"MoneyOperations\"", itemsCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Company\"", itemsCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Description\"", itemsCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Purpose\"", itemsCommand, StringComparison.Ordinal);
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class CashOrderConcurrentReadGate
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class CashOrderConcurrentReadInterceptor(CashOrderConcurrentReadGate gate) : DbCommandInterceptor
    {
        private bool _waited;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_waited && command.CommandText.Contains("FROM \"MoneyOperations\" AS", StringComparison.Ordinal))
            {
                _waited = true;
                await gate.ArriveAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class InventoryPostgresFailureInterceptor(string sqlState) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("UPDATE \"MoneyAccounts\"", StringComparison.Ordinal))
                throw new Npgsql.PostgresException("synthetic failure", "ERROR", "ERROR", sqlState);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InventoryTransientFailureInterceptor(int code, bool wrapped) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !Triggered && command.CommandText.Contains("UPDATE \"MoneyAccounts\"", StringComparison.Ordinal))
            {
                Triggered = true;
                var error = new SqliteException("synthetic database conflict", code);
                if (wrapped) throw new DbUpdateException("synthetic wrapped conflict", error);
                throw error;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InventoryStaleLockInterceptor(string target) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !Triggered && command.CommandText.Contains("UPDATE \"MoneyAccounts\"", StringComparison.Ordinal))
            {
                Triggered = true;
                await using var change = command.Connection!.CreateCommand();
                change.Transaction = command.Transaction;
                change.CommandText = target switch
                {
                    "account" => "UPDATE \"MoneyAccounts\" SET \"IsActive\" = 0",
                    "permission" => "UPDATE \"RolePermissions\" SET \"CanEdit\" = 0",
                    _ => "DELETE FROM \"UserCompanyAccesses\"",
                };
                await change.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CashOrderStaleLockInterceptor(string target) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var table = target == "account" ? "MoneyAccounts" : "MoneyOperations";
            if (Enabled && !Triggered && command.CommandText.Contains($"UPDATE \"{table}\"", StringComparison.Ordinal))
            {
                Triggered = true;
                await using var change = command.Connection!.CreateCommand();
                change.Transaction = command.Transaction;
                change.CommandText = target switch
                {
                    "account" => "UPDATE \"MoneyAccounts\" SET \"IsActive\" = 0",
                    "rights" => "UPDATE \"RolePermissions\" SET \"CanCreate\" = 0",
                    _ => "UPDATE \"MoneyOperations\" SET \"Amount\" = 200",
                };
                await change.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PostBoundCashOrder_RequiresPermissionAndCompanyAccess(bool canEdit, bool hasCompanyAccess)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: canEdit);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "SYN-POST",
            Status = CashOrderStatus.Draft,
            Amount = 100m,
            Currency = "RUB",
            Account = account.Name,
            MoneyAccountId = account.Id,
            CompanyId = account.CompanyId,
            CreatedByUserId = userId
        });
        await db.SaveChangesAsync();
        if (!hasCompanyAccess)
        {
            db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync());
            await db.SaveChangesAsync();
        }
        var order = await db.CashOrders.SingleAsync();
        var result = await CreateController(db, userId, isAdminClaim: false).Post(order.Id, CancellationToken.None);
        if (canEdit && hasCompanyAccess)
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(CashOrderStatus.Posted, order.Status);
        }
        else
        {
            Assert.True(result is ForbidResult or NotFoundResult);
            Assert.Equal(CashOrderStatus.Draft, order.Status);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CopyBoundCashOrder_RequiresPermissionAndCompanyAccess(bool canEdit, bool hasCompanyAccess)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canEdit: canEdit);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "SYN-COPY",
            Status = CashOrderStatus.Posted,
            Amount = 100m,
            Currency = "RUB",
            Account = account.Name,
            MoneyAccountId = account.Id,
            CompanyId = account.CompanyId,
            CreatedByUserId = userId
        });
        await db.SaveChangesAsync();
        if (!hasCompanyAccess)
        {
            db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync());
            await db.SaveChangesAsync();
        }
        var result = await CreateController(db, userId, isAdminClaim: false).Copy((await db.CashOrders.SingleAsync()).Id, CancellationToken.None);
        if (canEdit && hasCompanyAccess)
        {
            Assert.IsType<CreatedResult>(result);
            Assert.Equal(2, await db.CashOrders.CountAsync());
        }
        else
        {
            Assert.True(result is ForbidResult or NotFoundResult);
            Assert.Single(await db.CashOrders.ToListAsync());
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CreateManualOrder_RequiresCurrentPermissionAndCompanyAccess(bool canCreate, bool hasCompanyAccess)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserWithPaymentsPermissionAsync(db, canView: true, canCreate: canCreate);
        await AddConfirmedCashAccountAsync(db, userId, "Касса");
        var account = await db.MoneyAccounts.SingleAsync();
        if (!hasCompanyAccess)
        {
            db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync());
            await db.SaveChangesAsync();
        }
        var controller = CreateController(db, userId, isAdminClaim: false);
        var result = await controller.Create(new CashOrder
        {
            Number = "SYN-PERMISSION",
            Status = CashOrderStatus.Draft,
            Amount = 100m,
            Currency = "RUB",
            SelectedMoneyAccountId = account.Id
        }, CancellationToken.None);
        if (canCreate && hasCompanyAccess)
        {
            Assert.IsType<CreatedResult>(result);
            var saved = Assert.Single(await db.CashOrders.ToListAsync());
            Assert.Equal(account.Id, saved.MoneyAccountId);
            Assert.Equal(account.CompanyId, saved.CompanyId);
        }
        else
        {
            Assert.True(result is ForbidResult or BadRequestObjectResult);
            Assert.Empty(await db.CashOrders.ToListAsync());
        }
    }

    private static async Task AddConfirmedCashAccountAsync(PaymentContext db, int userId, string accountName)
    {
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.IsEmployee = true;
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = company.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        db.MoneyAccounts.Add(new MoneyAccount { Name = accountName, Source = MoneyOperationSource.Cash, CompanyId = company.Id, IsActive = true });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CreateUserWithTimeZoneAsync(PaymentContext db, string timeZoneId)
    {
        var role = new Role
        {
            Name = $"cash-order-timezone-role-{Guid.NewGuid():N}",
            Description = "Cash order timezone test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"cash-order-timezone-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Cash order timezone user",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static async Task<int> CreateUserWithPaymentsPermissionAsync(
        PaymentContext db,
        bool canView = false,
        bool canCreate = false,
        bool canEdit = false,
        bool canDelete = false,
        bool canExport = false,
        string viewScope = DataVisibilityScope.All)
    {
        var role = new Role
        {
            Name = $"cash-order-permissions-{Guid.NewGuid():N}",
            Description = "Cash order permissions test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"cash-order-permissions-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Cash order permissions user",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.RolePermissions.Add(new RolePermission
        {
            Role = role,
            Section = RolePermissionCatalog.Payments,
            CanView = canView,
            CanCreate = canCreate,
            CanEdit = canEdit,
            CanDelete = canDelete,
            CanExport = canExport,
            ViewScope = viewScope,
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static CashOrdersController CreateController(PaymentContext db, int userId, bool isAdminClaim = true)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        isAdminClaim
                            ?
                            [
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                                new Claim(ClaimTypes.Role, "admin"),
                            ]
                            :
                            [
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            ],
                        authenticationType: "Test")),
                },
            },
        };

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }
}
