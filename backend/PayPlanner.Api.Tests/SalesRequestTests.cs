using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SalesRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualTask_RequiresAssigneeAccessToTheSpecificRequest(bool ownsRequest)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), ownsRequest ? f.Manager.Id : f.Admin.Id, default)).Value!;
        var before = await f.Db.TaskJournalItems.CountAsync();
        var result = await f.Service.CreateManualTaskAsync(request.Id, f.Manager.Id, "Private request task", null, f.Admin.Id, default);
        Assert.Equal(ownsRequest, result.Ok);
        Assert.Equal(before + (ownsRequest ? 1 : 0), await f.Db.TaskJournalItems.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuleTask_RequiresFixedAssigneeAccessToTheSpecificRequest(bool ownsRequest)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var firstStatus = await f.Db.SalesRequestStatuses.OrderBy(status => status.SortOrder).FirstAsync();
        Assert.True((await f.Service.ReplaceRulesAsync(firstStatus.Id, [new("Private rule", f.Manager.Id, 60)], default)).Ok);
        var result = await f.Service.CreateAsync(NewRequest(f.Client), ownsRequest ? f.Manager.Id : f.Admin.Id, default);
        Assert.True(result.Ok);
        var tasks = await f.Db.TaskJournalItems.Where(task => task.SalesRequestId == result.Value!.Id).ToArrayAsync();
        Assert.Equal(ownsRequest ? 1 : 0, tasks.Length);
    }

    private sealed record Fixture(PaymentContext Db, SalesRequestService Service, User Manager, User Admin, Client Client);

    private static async Task<Fixture> CreateAsync()
    {
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var adminRole = new Role { Name = "admin", Description = "admin" };
        var managerRole = new Role
        {
            Name = "manager",
            Description = "manager",
            Permissions = { new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own } },
        };
        var admin = new User { Email = "a@x.test", FullName = "Админ", PasswordHash = "x", Role = adminRole, IsActive = true, IsApproved = true };
        var manager = new User { Email = "m@x.test", FullName = "Аня", PasswordHash = "x", Role = managerRole, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Иванов Иван", Phone = "+7 (900) 111-22-33" };
        db.AddRange(admin, manager, client);
        await db.SaveChangesAsync();
        return new Fixture(db, new SalesRequestService(db), manager, admin, client);
    }

    private static SalesRequestCreateInput NewRequest(Client client, string source = "Call")
        => new(source, client.Id, null, null, null, null, null, "Спор с застройщиком", null, null, null);

    private static int StatusId(PaymentContext db, string key) => db.SalesRequestStatuses.Single(item => item.Key == key).Id;

    private static async Task CloseRequestTasksAsync(PaymentContext db, params int[] requestIds)
    {
        var tasks = await db.TaskJournalItems.Where(task => task.SalesRequestId.HasValue && requestIds.Contains(task.SalesRequestId.Value)).ToListAsync();
        foreach (var task in tasks)
        {
            task.Status = "cancelled";
            task.Result = "Отменена сотрудником";
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Payment> PreparePaidInvoiceAsync(Fixture f, SalesRequest request, decimal amount = 100m)
    {
        f.Manager.IsEmployee = true;
        foreach (var section in new[] { RolePermissionCatalog.Accounts, RolePermissionCatalog.Payments })
        {
            if (f.Manager.Role!.Permissions.All(permission => permission.Section != section))
                f.Manager.Role.Permissions.Add(new RolePermission { Section = section, CanView = true, ViewScope = DataVisibilityScope.All });
        }
        var company = new LegalEntity { ShortName = "Тестовая компания" };
        var invoice = new Invoice { Number = $"СЧ-{request.Id}", Date = new DateTime(2026, 1, 1) };
        f.Db.AddRange(company, invoice);
        await f.Db.SaveChangesAsync();
        var setting = await f.Db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "legalEntities.mainCompanyIds");
        if (setting is null) f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        else
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(setting.Value) ?? [];
            ids.Add(company.Id);
            setting.Value = System.Text.Json.JsonSerializer.Serialize(ids);
        }
        f.Db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = f.Manager.Id, LegalEntityId = company.Id });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = $"Счёт {request.Id}" };
        var payment = new Payment { Invoice = invoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = amount, PaidAmount = amount };
        f.Db.AddRange(account, payment);
        await f.Db.SaveChangesAsync();
        f.Db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = amount, ResponsibleUserId = f.Manager.Id });
        await f.Db.SaveChangesAsync();
        var bound = await f.Service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, f.Manager.Id, default);
        Assert.True(bound.Ok, bound.Error);
        return payment;
    }

    [Fact]
    public async Task SuccessfulTransition_RejectsUnlinkedPaymentEvenForSameClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var unrelated = new Payment { ClientId = f.Client.Id, Type = PaymentType.Income, Amount = 100m, PaidAmount = 100m };
        f.Db.Payments.Add(unrelated);
        await f.Db.SaveChangesAsync();
        var paid = StatusId(f.Db, "paid");
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, unrelated.Id, false, null), f.Admin.Id, default);
        Assert.False(result.Ok);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Fact]
    public async Task SuccessfulTransition_RejectsAnotherInvoiceOfSameClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var company = new LegalEntity { ShortName = "Компания" };
        var invoice = new Invoice { Number = "СЧ-1", Date = new DateTime(2026, 1, 1) };
        var otherInvoice = new Invoice { Number = "СЧ-2", Date = new DateTime(2026, 1, 1) };
        f.Db.AddRange(company, invoice, otherInvoice);
        await f.Db.SaveChangesAsync();
        f.Db.Payments.AddRange(
            new Payment { Invoice = invoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = otherInvoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 100m });
        await f.Db.SaveChangesAsync();
        var binding = await f.Service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, f.Admin.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var unrelatedId = await f.Db.Payments.Where(payment => payment.InvoiceId == otherInvoice.Id).Select(payment => payment.Id).SingleAsync();
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), binding.Value!.Version, unrelatedId, false, null), f.Admin.Id, default);
        Assert.False(result.Ok);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Fact]
    public async Task ServiceInvoice_CannotBeReusedForAnotherRequest()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var first = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var second = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, first);
        var result = await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, f.Manager.Id, default);
        Assert.Equal(409, result.Status);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == second.Id)).ServiceInvoiceId);
    }

    [Fact]
    public async Task SharedServiceInvoice_AllocatesAndCompletesEachRequestAfterFullPayment()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var first = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var second = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, first.Id, second.Id);
        var payment = await PreparePaidInvoiceAsync(f, first);
        var firstShared = await f.Service.SetServiceInvoiceAsync(first.Id, first.Version, payment.InvoiceId, f.Manager.Id, default, 60m);
        Assert.True(firstShared.Ok, firstShared.Error);
        var secondShared = await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, f.Manager.Id, default, 40m);
        Assert.True(secondShared.Ok, secondShared.Error);
        var visible = await ControllerFor(f.Db, f.Manager).GetServiceInvoice(first.Id, default);
        var link = Assert.IsType<SalesRequestServiceInvoiceDto>(Assert.IsType<OkObjectResult>(visible.Result).Value);
        Assert.Equal(60m, link.AllocationAmount);
        var receipt = await f.Db.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == payment.InvoiceId);
        receipt.Amount = 99m;
        await f.Db.SaveChangesAsync();
        var paid = StatusId(f.Db, "paid");
        Assert.False((await f.Service.TransitionAsync(first.Id, new SalesRequestTransitionInput(paid, firstShared.Value!.Version, null, false, null), f.Manager.Id, default)).Ok);
        receipt.Amount = 100m;
        await f.Db.SaveChangesAsync();
        var doneFirst = await f.Service.TransitionAsync(first.Id, new SalesRequestTransitionInput(paid, firstShared.Value!.Version, null, false, null), f.Manager.Id, default);
        var doneSecond = await f.Service.TransitionAsync(second.Id, new SalesRequestTransitionInput(paid, secondShared.Value!.Version, null, false, null), f.Manager.Id, default);
        Assert.True(doneFirst.Ok, doneFirst.Error);
        Assert.True(doneSecond.Ok, doneSecond.Error);
        Assert.Equal(2, await f.Db.ClientCases.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(40.001)]
    [InlineData(40.01)]
    public async Task SharedServiceInvoice_RejectsInvalidOrExcessAllocation(decimal amount)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var first = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var second = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, first);
        Assert.True((await f.Service.SetServiceInvoiceAsync(first.Id, first.Version, payment.InvoiceId, f.Manager.Id, default, 60m)).Ok);
        var result = await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, f.Manager.Id, default, amount);
        Assert.False(result.Ok);
        Assert.Null(result.Details);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == second.Id)).ServiceInvoiceId);
        Assert.False((await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, f.Manager.Id, default)).Ok);
    }

    [Fact]
    public async Task SharedServiceInvoice_RechecksAllocationsWhenInvoiceAmountChanges()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var first = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var second = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, first.Id, second.Id);
        var payment = await PreparePaidInvoiceAsync(f, first);
        Assert.True((await f.Service.SetServiceInvoiceAsync(first.Id, first.Version, payment.InvoiceId, f.Manager.Id, default, 60m)).Ok);
        Assert.True((await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, f.Manager.Id, default, 40m)).Ok);
        payment.Amount = 90m;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.TransitionAsync(first.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), first.Version, null, false, null), f.Manager.Id, default);
        Assert.Equal(409, result.Status);
        Assert.False(await f.Db.ClientCases.AnyAsync());
    }

    [Fact]
    public async Task SharedServiceInvoice_DoesNotExposeAnotherManagersRequestOrShare()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var first = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, first);
        Assert.True((await f.Service.SetServiceInvoiceAsync(first.Id, first.Version, payment.InvoiceId, f.Manager.Id, default, 60m)).Ok);
        var colleague = new User
        {
            Email = "other-manager@example.test",
            FullName = "Другой",
            PasswordHash = "x",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            Role = new Role
            {
                Name = "other-manager",
                Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All },
                new RolePermission { Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All },
            }
            },
        };
        f.Db.Users.Add(colleague);
        await f.Db.SaveChangesAsync();
        f.Db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = colleague.Id, LegalEntityId = payment.SellerCompanyId!.Value });
        await f.Db.SaveChangesAsync();
        var second = (await f.Service.CreateAsync(NewRequest(f.Client), colleague.Id, default)).Value!;
        var rejected = await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, colleague.Id, default, 41m);
        Assert.Equal(409, rejected.Status);
        Assert.Null(rejected.Details);
        Assert.DoesNotContain("60", rejected.Error);
        Assert.IsType<NotFoundResult>((await ControllerFor(f.Db, colleague).Get(first.Id, default)).Result);
        var accepted = await f.Service.SetServiceInvoiceAsync(second.Id, second.Version, payment.InvoiceId, colleague.Id, default, 40m);
        Assert.True(accepted.Ok, accepted.Error);
    }

    [Theory]
    [InlineData(99.99, false, false, 99.99)]
    [InlineData(100, true, false, 100)]
    [InlineData(100, true, true, 0)]
    public async Task SuccessfulTransition_RequiresFullMatchedServiceInvoiceReceipt(decimal received, bool expected, bool omitPaymentId, decimal recordedPaid)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var company = new LegalEntity { ShortName = "Компания" };
        var invoice = new Invoice { Number = "СЧ-ПОЛНАЯ", Date = new DateTime(2026, 1, 1) };
        f.Db.AddRange(company, invoice);
        await f.Db.SaveChangesAsync();
        f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Тестовый счёт" };
        var payment = new Payment { Invoice = invoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = recordedPaid };
        f.Db.AddRange(account, payment);
        await f.Db.SaveChangesAsync();
        f.Db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = received, ResponsibleUserId = f.Admin.Id });
        await f.Db.SaveChangesAsync();
        var binding = await f.Service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, f.Admin.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), binding.Value!.Version, omitPaymentId ? null : payment.Id, false, null), f.Admin.Id, default);
        Assert.Equal(expected, result.Ok);
        Assert.Equal(expected, (await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId.HasValue);
    }

    [Theory]
    [InlineData(RolePermissionCatalog.Accounts)]
    [InlineData(RolePermissionCatalog.Payments)]
    public async Task SuccessfulTransition_RejectsRevokedFinancialView(string section)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        f.Manager.IsEmployee = true;
        f.Manager.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All });
        f.Manager.Role.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All });
        var company = new LegalEntity { ShortName = "Компания" };
        var invoice = new Invoice { Number = "СЧ-ПРАВА", Date = new DateTime(2026, 1, 1) };
        f.Db.AddRange(company, invoice);
        await f.Db.SaveChangesAsync();
        f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        f.Db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = f.Manager.Id, LegalEntityId = company.Id });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var payment = new Payment { Invoice = invoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 100m };
        f.Db.AddRange(account, payment);
        await f.Db.SaveChangesAsync();
        f.Db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m, ResponsibleUserId = f.Manager.Id });
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var binding = await f.Service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, f.Manager.Id, default);
        Assert.True(binding.Ok, binding.Error);
        f.Manager.Role.Permissions.Single(permission => permission.Section == section).CanView = false;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), binding.Value!.Version, payment.Id, false, null), f.Manager.Id, default);
        Assert.False(result.Ok);
        Assert.Null(result.Details);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("unmatched")]
    [InlineData("currency")]
    [InlineData("foreign-company")]
    [InlineData("refund")]
    public async Task SuccessfulTransition_RejectsUnprovenOrRefundedReceipt(string scenario)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var payment = await PreparePaidInvoiceAsync(f, request);
        var operation = await f.Db.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == payment.InvoiceId);
        if (scenario == "plan") operation.AccountingType = MoneyOperationAccountingType.Plan;
        if (scenario == "unmatched") operation.MatchStatus = MoneyOperationMatchStatus.Ignored;
        if (scenario == "currency") operation.Currency = "USD";
        if (scenario == "foreign-company")
        {
            var other = new LegalEntity { ShortName = "Другая компания" };
            f.Db.LegalEntities.Add(other);
            await f.Db.SaveChangesAsync();
            (await f.Db.MoneyAccounts.SingleAsync(item => item.Id == operation.MoneyAccountId)).CompanyId = other.Id;
        }
        if (scenario == "refund")
            f.Db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = payment.InvoiceId, MoneyAccountId = operation.MoneyAccountId, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Expense, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 1m, ResponsibleUserId = f.Manager.Id });
        await f.Db.SaveChangesAsync();
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, false, null), f.Manager.Id, default);
        Assert.Equal(409, result.Status);
        Assert.Null(result.Details);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulTransition_DoesNotCountCashOrderWithoutOrInAdditionToMovement(bool linkedMovement)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var payment = await PreparePaidInvoiceAsync(f, request);
        var operation = await f.Db.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == payment.InvoiceId);
        if (linkedMovement) operation.Amount = 50m;
        else f.Db.MoneyOperations.Remove(operation);
        f.Db.CashOrders.Add(new CashOrder { Number = "ТЕСТ-ПКО", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted, Date = DateTime.UtcNow, Amount = linkedMovement ? 50m : 100m, Currency = "RUB", ClientId = f.Client.Id, PaymentId = payment.Id, InvoiceId = payment.InvoiceId, MoneyOperationId = linkedMovement ? operation.Id : null });
        await f.Db.SaveChangesAsync();
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, null, false, null), f.Manager.Id, default);
        Assert.Equal(409, result.Status);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SuccessfulTransition_HandlesDeletedReceiptAndRefundSafely(bool deletedRefund, bool expected)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var payment = await PreparePaidInvoiceAsync(f, request);
        var original = await f.Db.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == payment.InvoiceId);
        var additional = new MoneyOperation { LinkedInvoiceId = payment.InvoiceId, MoneyAccountId = original.MoneyAccountId, Source = MoneyOperationSource.Bank, Direction = deletedRefund ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = deletedRefund ? 1m : 100m, ResponsibleUserId = f.Manager.Id };
        f.Db.MoneyOperations.Add(additional);
        await f.Db.SaveChangesAsync();
        f.Db.MoneyOperations.Remove(deletedRefund ? additional : original);
        await f.Db.SaveChangesAsync();
        var result = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, null, false, null), f.Manager.Id, default);
        Assert.Equal(expected, result.Ok);
        Assert.Equal(expected, (await f.Db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId.HasValue);
    }

    [Fact]
    public async Task RequestTasks_StayPrivateEvenWithBroadTaskAndCalendarAccess()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var lawyer = new User
        {
            Email = "lawyer@x.test",
            FullName = "Юрист",
            PasswordHash = "x",
            IsActive = true,
            IsApproved = true,
            Role = new Role { Name = "lawyer", Description = "lawyer" }
        };
        f.Db.Users.Add(lawyer);
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        Assert.Equal(400, (await f.Service.CreateAsync(NewRequest(f.Client) with { ResponsibleUserId = lawyer.Id }, f.Manager.Id, default)).Status);
        Assert.False((await f.Service.CreateManualTaskAsync(request.Id, lawyer.Id, "Продажи", null, f.Manager.Id, default)).Ok);
        foreach (var section in new[] { RolePermissionCatalog.Tasks, RolePermissionCatalog.Calendar })
        {
            var denied = new DataVisibilityAccess(lawyer.Id, false, section, DataVisibilityScope.All, [lawyer.Id]);
            Assert.Empty(await f.Db.TaskJournalItems.ApplyTaskVisibility(denied, f.Db).ToListAsync());
            var owner = new DataVisibilityAccess(f.Manager.Id, false, section, DataVisibilityScope.All, [f.Manager.Id]);
            Assert.Single(await f.Db.TaskJournalItems.ApplyTaskVisibility(owner, f.Db).ToListAsync());
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task RequestTasks_DepartmentScopeMatchesRequestAccess(bool memberActive, bool sameDepartment, bool useDictionary)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        f.Manager.Department = useDictionary ? null : "  Продажи  ";
        f.Admin.Department = useDictionary ? null : sameDepartment ? "Продажи" : "Другой отдел";
        if (useDictionary)
        {
            var department = new DepartmentDictionaryItem { Name = "Продажи" };
            var other = new DepartmentDictionaryItem { Name = "Другой отдел" };
            f.Db.AddRange(department, other);
            await f.Db.SaveChangesAsync();
            f.Manager.DepartmentId = department.Id;
            f.Admin.DepartmentId = sameDepartment ? department.Id : other.Id;
        }
        f.Manager.Role!.Permissions.Single().ViewScope = DataVisibilityScope.Department;
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        f.Admin.IsActive = memberActive;
        await f.Db.SaveChangesAsync();
        var access = await f.Db.ResolveDataVisibilityAsync(ControllerFor(f.Db, f.Manager).User, RolePermissionCatalog.Requests, default);
        var requestVisible = await f.Db.SalesRequests.ApplySalesRequestVisibility(access).AnyAsync(item => item.Id == request.Id);
        Assert.Equal(memberActive && sameDepartment, requestVisible);
        var taskAccess = new DataVisibilityAccess(f.Manager.Id, false, RolePermissionCatalog.Tasks, DataVisibilityScope.All, [f.Manager.Id]);
        var taskVisible = await f.Db.TaskJournalItems.ApplyTaskVisibility(taskAccess, f.Db).AnyAsync(item => item.SalesRequestId == request.Id);
        Assert.Equal(requestVisible, taskVisible);
    }

    [Fact]
    public async Task Automation_ProcessesEligibleRequestsBeyondFirstBatch()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var stage = StatusId(f.Db, "documents-prep");
        await f.Service.SetSlaAsync(stage, 60, default);
        var requests = Enumerable.Range(0, 501).Select(i => new SalesRequest
        {
            ClientId = f.Client.Id,
            ResponsibleUserId = f.Manager.Id,
            CreatedByUserId = f.Manager.Id,
            StatusId = stage,
            Summary = $"Обращение {i}",
            StatusEnteredAt = DateTime.UtcNow.AddHours(-2),
        }).ToList();
        f.Db.SalesRequests.AddRange(requests);
        await f.Db.SaveChangesAsync();
        f.Db.TaskJournalItems.AddRange(requests.Select((r, i) => new TaskJournalItem
        {
            SalesRequestId = r.Id,
            Title = "Подготовить документы",
            Status = i == 500 ? "done" : "new",
            TagsJson = System.Text.Json.JsonSerializer.Serialize(new[] { $"request-status:{stage}" }),
        }));
        await f.Db.SaveChangesAsync();
        Assert.Equal(501, await f.Service.MarkSlaBreachesAsync(default));
        Assert.Equal(0, await f.Service.MarkSlaBreachesAsync(default));
        Assert.Equal(1, await f.Service.AdvanceCompletedAsync(default));
        Assert.Equal(StatusId(f.Db, "documents-sent"), (await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == requests[500].Id)).StatusId);
    }

    [Fact]
    public async Task Creation_RejectsInvalidInputWithoutCreatingClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var input = NewRequest(f.Client) with { ClientId = null, NewClientName = "Новый", NewClientPhone = "89005554433", Type = "invalid" };
        Assert.Equal(400, (await f.Service.CreateAsync(input, f.Manager.Id, default)).Status);
        Assert.Equal(1, await f.Db.Clients.CountAsync());
        Assert.Equal(400, (await f.Service.CreateAsync(NewRequest(f.Client) with { Summary = new string('x', 4001) }, f.Manager.Id, default)).Status);
        Assert.Equal(404, (await f.Service.CreateAsync(NewRequest(f.Client) with { ResponsibleUserId = 99999 }, f.Manager.Id, default)).Status);
    }

    [Fact]
    public async Task Create_ValidatesFieldsCreatesFirstStatusTaskAndEvent()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;

        Assert.False((await f.Service.CreateAsync(NewRequest(f.Client, "Радио"), f.Manager.Id, default)).Ok);
        Assert.False((await f.Service.CreateAsync(NewRequest(f.Client) with { Summary = "  " }, f.Manager.Id, default)).Ok);

        var created = await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default);

        Assert.True(created.Ok);
        var request = created.Value!;
        Assert.Equal("Новая", f.Db.SalesRequestStatuses.Single(s => s.Id == request.StatusId).Title);
        Assert.Equal(f.Manager.Id, request.ResponsibleUserId);
        Assert.Equal("New", request.Type);
        Assert.Contains("Клиент без истории дел", request.Comment);
        var task = Assert.Single(f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id));
        Assert.Equal(f.Manager.Id, task.AssigneeUserId);
        Assert.Equal(["Created", "TaskCreated"], f.Db.SalesRequestEvents.OrderBy(e => e.Id).Select(e => e.Kind).ToList());
    }

    [Fact]
    public async Task NewClient_IsCreatedOnce_AndPhoneDuplicateIsBlocked()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var input = new SalesRequestCreateInput("Avito", null, "Петров Пётр", "8 900 555 44 33", null, null, null, "Развод", null, null, null);

        var first = await f.Service.CreateAsync(input, f.Manager.Id, default);
        var duplicate = await f.Service.CreateAsync(input with { NewClientName = "Петров П.", NewClientPhone = "+7 (900) 555-44-33" }, f.Manager.Id, default);
        var existingByPhone = await f.Service.CreateAsync(input with { NewClientName = "Иван", NewClientPhone = "89001112233" }, f.Manager.Id, default);

        Assert.True(first.Ok);
        Assert.Equal(409, duplicate.Status);
        Assert.Equal(409, existingByPhone.Status);
        Assert.Equal(1, await f.Db.Clients.CountAsync(c => c.Name == "Петров Пётр"));
        Assert.False((await f.Service.CreateAsync(input with { NewClientName = "Коротко", NewClientPhone = "123" }, f.Manager.Id, default)).Ok);
    }

    [Fact]
    public async Task CaseLink_RequiresClientsCase_AndMarksClosedCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var other = new Client { Name = "Другой", Phone = "+79000000000" };
        var closed = new ClientCase { ClientId = f.Client.Id, Status = ClientCaseStatus.Closed, CaseNumber = "87", Title = "Старое" };
        var foreign = new ClientCase { ClientId = other.Id, CaseNumber = "88", Title = "Чужое" };
        f.Db.AddRange(other, closed);
        await f.Db.SaveChangesAsync();
        foreign.ClientId = other.Id;
        f.Db.ClientCases.Add(foreign);
        await f.Db.SaveChangesAsync();

        var ok = await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = closed.Id }, f.Manager.Id, default);
        var denied = await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = foreign.Id }, f.Manager.Id, default);

        Assert.True(ok.Value!.LinkedCaseWasClosed);
        Assert.Equal("PerCase", ok.Value.Type);
        Assert.Equal(404, denied.Status);
    }

    [Fact]
    public async Task Transition_ClosesPreviousStatusTasksAndCreatesNextOnes()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;

        var moved = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), request.Version, null, false, null), f.Manager.Id, default);

        Assert.True(moved.Ok);
        var tasks = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id).OrderBy(t => t.Id).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("cancelled", tasks[0].Status);
        Assert.Contains("сменой этапа", tasks[0].Result);
        Assert.Equal("new", tasks[1].Status);
        Assert.Contains("Квалифицировать", tasks[1].Title);
    }

    [Fact]
    public async Task StaleVersion_IsRejected_AndFinalRequestDoesNotMove()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var version = request.Version;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), version, null, false, null), f.Manager.Id, default);

        var stale = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "calculation"), version, null, false, null), f.Manager.Id, default);
        var fresh = (await f.Db.SalesRequests.FindAsync(request.Id))!;
        var declined = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "declined"), fresh.Version, null, false, null), f.Manager.Id, default);
        var afterFinal = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "calculation"), declined.Value!.Version, null, false, null), f.Manager.Id, default);

        Assert.Equal(409, stale.Status);
        Assert.Equal("Failure", declined.Value.FinalKind);
        Assert.Equal(409, afterFinal.Status);
    }

    [Fact]
    public async Task FinalStatus_BlocksOnForeignOpenTasks_AndForceCloseMarksThemCancelled()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.CreateManualTaskAsync(request.Id, f.Admin.Id, "Уточнить реквизиты", null, f.Manager.Id, default);
        var target = StatusId(f.Db, "abandoned");

        var blocked = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, false, null), f.Manager.Id, default);
        var forced = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, true, null), f.Manager.Id, default);

        Assert.Equal(409, blocked.Status);
        Assert.NotNull(blocked.Details);
        Assert.True(forced.Ok);
        var manual = f.Db.TaskJournalItems.Single(t => t.Title == "Уточнить реквизиты");
        Assert.Equal("cancelled", manual.Status);
        Assert.Contains("принудительно", manual.Result);
        Assert.Contains(f.Db.SalesRequestEvents, e => e.Kind == "TasksForceClosed");
        Assert.Contains(f.Db.TaskJournalItems, t => t.Title.StartsWith("Дожим") && t.Status == "new");
        Assert.NotNull(forced.Value!.ClosedAt);
    }

    [Fact]
    public async Task InvoiceCandidates_ReturnsOnlyUnambiguousIncomeInvoicesForRequestClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var company = new LegalEntity { ShortName = "Компания" };
        var other = new Client { Name = "Другой", Phone = "+79000000001" };
        f.Db.AddRange(company, other);
        await f.Db.SaveChangesAsync();
        var allowed = new Invoice { Number = "СЧ-100", Date = new DateTime(2026, 1, 1) };
        var unrelated = new Invoice { Number = "СЧ-200", Date = new DateTime(2026, 1, 2) };
        var ambiguous = new Invoice { Number = "СЧ-300", Date = new DateTime(2026, 1, 3) };
        f.Db.Payments.AddRange(
            new Payment { Invoice = allowed, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = unrelated, ClientId = other.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = ambiguous, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = ambiguous, ClientId = other.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await f.Db.SaveChangesAsync();
        var response = await ControllerFor(f.Db, f.Admin, admin: true).InvoiceCandidates(request.Id, "СЧ", 0, 20, default);
        var page = Assert.IsType<SalesRequestInvoiceCandidatesDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var candidate = Assert.Single(page.Items);
        Assert.Equal(allowed.Id, candidate.InvoiceId);
        Assert.Equal("СЧ-100", candidate.Number);
        Assert.Equal(company.Id, candidate.SellerCompanyId);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task InvoiceCandidates_DistinguishesEqualNumbersByInvoiceAndCompany()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var firstCompany = new LegalEntity { ShortName = "Первая" };
        var secondCompany = new LegalEntity { ShortName = "Вторая" };
        f.Db.AddRange(firstCompany, secondCompany);
        await f.Db.SaveChangesAsync();
        var first = new Invoice { Number = "СЧ-ОДИНАКОВЫЙ", Date = new DateTime(2026, 1, 1) };
        var second = new Invoice { Number = "СЧ-ОДИНАКОВЫЙ", Date = new DateTime(2026, 1, 1) };
        f.Db.Payments.AddRange(
            new Payment { Invoice = first, ClientId = f.Client.Id, SellerCompanyId = firstCompany.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = second, ClientId = f.Client.Id, SellerCompanyId = secondCompany.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await f.Db.SaveChangesAsync();
        var result = await ControllerFor(f.Db, f.Admin, admin: true).InvoiceCandidates(request.Id, "СЧ", 0, 20, default);
        var page = Assert.IsType<SalesRequestInvoiceCandidatesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, item => item.InvoiceId == first.Id && item.SellerCompanyId == firstCompany.Id);
        Assert.Contains(page.Items, item => item.InvoiceId == second.Id && item.SellerCompanyId == secondCompany.Id);
    }

    [Fact]
    public async Task ServiceInvoice_RedactsUnavailableLinkAndPreventsSilentReplacement()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        f.Manager.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All });
        var company = new LegalEntity { ShortName = "Компания" };
        f.Db.LegalEntities.Add(company);
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var oldInvoice = new Invoice { Number = "ЗАКРЫТЫЙ-1", Date = new DateTime(2026, 1, 1) };
        var newInvoice = new Invoice { Number = "НОВЫЙ-2", Date = new DateTime(2026, 1, 2) };
        var oldHeader = new Payment { Invoice = oldInvoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        f.Db.Payments.AddRange(oldHeader, new Payment { Invoice = newInvoice, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await f.Db.SaveChangesAsync();
        var controller = ControllerFor(f.Db, f.Manager);
        var bound = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = request.Version, InvoiceId = oldInvoice.Id }, default);
        Assert.IsType<OkObjectResult>(bound.Result);
        var shown = await controller.GetServiceInvoice(request.Id, default);
        var visible = Assert.IsType<SalesRequestServiceInvoiceDto>(Assert.IsType<OkObjectResult>(shown.Result).Value);
        Assert.Equal("available", visible.State);
        Assert.Equal(oldInvoice.Id, visible.Invoice!.InvoiceId);
        Assert.Equal(company.Id, visible.Invoice.SellerCompanyId);
        oldHeader.Status = PaymentStatus.Cancelled;
        await f.Db.SaveChangesAsync();
        var hidden = await controller.GetServiceInvoice(request.Id, default);
        var protectedLink = Assert.IsType<SalesRequestServiceInvoiceDto>(Assert.IsType<OkObjectResult>(hidden.Result).Value);
        Assert.Equal("unavailable", protectedLink.State);
        Assert.Null(protectedLink.Invoice);
        Assert.DoesNotContain("ЗАКРЫТЫЙ-1", System.Text.Json.JsonSerializer.Serialize(protectedLink), StringComparison.Ordinal);
        var replace = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = request.Version, InvoiceId = newInvoice.Id }, default);
        Assert.Equal(404, Assert.IsType<ObjectResult>(replace.Result).StatusCode);
        var remove = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = request.Version, Remove = true }, default);
        Assert.Equal(404, Assert.IsType<ObjectResult>(remove.Result).StatusCode);
        Assert.Equal(oldInvoice.Id, (await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
        f.Manager.Role.Permissions.Single(p => p.Section == RolePermissionCatalog.Accounts).CanView = false;
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await controller.GetServiceInvoice(request.Id, default)).Result);
    }

    [Fact]
    public async Task InvoiceCandidates_RequiresBothRequestEditAndInvoiceViewWithoutRevealingDetails()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var mine = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var foreign = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var controller = ControllerFor(f.Db, f.Manager);
        Assert.IsType<ForbidResult>((await controller.InvoiceCandidates(mine.Id, null, 0, 20, default)).Result);
        f.Manager.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All });
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await controller.InvoiceCandidates(foreign.Id, null, 0, 20, default)).Result);
        f.Manager.Role.Permissions.Single(p => p.Section == RolePermissionCatalog.Requests).CanEdit = false;
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>((await controller.InvoiceCandidates(mine.Id, null, 0, 20, default)).Result);
    }

    [Fact]
    public async Task ServiceInvoice_BindsOnlyVisibleClientInvoiceAndProtectsVersion()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var company = new LegalEntity { ShortName = "Компания" };
        var other = new Client { Name = "Другой", Phone = "+79000000001" };
        f.Db.AddRange(company, other);
        await f.Db.SaveChangesAsync();
        var allowed = new Invoice { Number = "СЧ-1", Date = new DateTime(2026, 1, 1) };
        var foreign = new Invoice { Number = "СЧ-2", Date = new DateTime(2026, 1, 2) };
        f.Db.Payments.AddRange(
            new Payment { Invoice = allowed, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = foreign, ClientId = other.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await f.Db.SaveChangesAsync();
        var controller = ControllerFor(f.Db, f.Admin, admin: true);
        var invalid = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = request.Version, InvoiceId = foreign.Id }, default);
        Assert.Equal(404, Assert.IsType<ObjectResult>(invalid.Result).StatusCode);
        var success = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = request.Version, InvoiceId = allowed.Id }, default);
        var dto = Assert.IsType<SalesRequestDto>(Assert.IsType<OkObjectResult>(success.Result).Value);
        Assert.DoesNotContain("ServiceInvoiceId", System.Text.Json.JsonSerializer.Serialize(dto), StringComparison.Ordinal);
        Assert.Equal(1, dto.Version);
        Assert.Equal(allowed.Id, (await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
        var empty = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = dto.Version }, default);
        Assert.IsType<BadRequestObjectResult>(empty.Result);
        Assert.Equal(allowed.Id, (await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
        var stale = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = 0, Remove = true }, default);
        Assert.Equal(409, Assert.IsType<ObjectResult>(stale.Result).StatusCode);
        var eventsBefore = await f.Db.SalesRequestEvents.CountAsync();
        var repeated = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = dto.Version, InvoiceId = allowed.Id }, default);
        Assert.IsType<OkObjectResult>(repeated.Result);
        Assert.Equal(eventsBefore, await f.Db.SalesRequestEvents.CountAsync());
        var removed = await controller.SetServiceInvoice(request.Id, new SalesRequestServiceInvoiceRequest { Version = dto.Version, Remove = true }, default);
        Assert.Equal(2, Assert.IsType<SalesRequestDto>(Assert.IsType<OkObjectResult>(removed.Result).Value).Version);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
    }

    [Fact]
    public async Task ServiceInvoice_DeniesEmployeeWithoutInvoiceView()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var result = await ControllerFor(f.Db, f.Manager).SetServiceInvoice(request.Id,
            new SalesRequestServiceInvoiceRequest { Version = request.Version, InvoiceId = 123 }, default);
        Assert.Equal(404, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Null((await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
        f.Manager.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All });
        f.Manager.Role.Permissions.Single(p => p.Section == RolePermissionCatalog.Requests).CanEdit = false;
        await f.Db.SaveChangesAsync();
        var revoked = await f.Service.SetServiceInvoiceAsync(request.Id, request.Version + 1, 123, f.Manager.Id, default);
        Assert.Equal(403, revoked.Status);
        Assert.Null(revoked.Details);
    }

    [Fact]
    public async Task InvoiceCandidates_UsesBoundedStablePagesAndValidatesLimits()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var company = new LegalEntity { ShortName = "Компания" };
        f.Db.LegalEntities.Add(company);
        await f.Db.SaveChangesAsync();
        var first = new Invoice { Number = "ПОИСК-1", Date = new DateTime(2026, 1, 1) };
        var second = new Invoice { Number = "ПОИСК-2", Date = new DateTime(2026, 1, 2) };
        f.Db.Payments.AddRange(
            new Payment { Invoice = first, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = first, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = second, ClientId = f.Client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await f.Db.SaveChangesAsync();
        var controller = ControllerFor(f.Db, f.Admin, admin: true);
        var firstResult = await controller.InvoiceCandidates(request.Id, "ПОИСК", 0, 1, default);
        var firstPage = Assert.IsType<SalesRequestInvoiceCandidatesDto>(Assert.IsType<OkObjectResult>(firstResult.Result).Value);
        Assert.Equal(first.Id, Assert.Single(firstPage.Items).InvoiceId);
        Assert.True(firstPage.HasMore);
        var nextResult = await controller.InvoiceCandidates(request.Id, "ПОИСК", first.Id, 1, default);
        var nextPage = Assert.IsType<SalesRequestInvoiceCandidatesDto>(Assert.IsType<OkObjectResult>(nextResult.Result).Value);
        Assert.Equal(second.Id, Assert.Single(nextPage.Items).InvoiceId);
        Assert.False(nextPage.HasMore);
        Assert.IsType<BadRequestObjectResult>((await controller.InvoiceCandidates(request.Id, null, 0, 21, default)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.InvoiceCandidates(request.Id, new string('x', 101), 0, 20, default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_DoesNotAutomaticallyCancelOpenRequestTasks(bool force)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var manual = (await f.Service.CreateManualTaskAsync(request.Id, f.Admin.Id, "Ручная задача", null, f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, request);
        var before = await f.Db.TaskJournalItems.AsNoTracking().Where(t => t.SalesRequestId == request.Id)
            .OrderBy(t => t.Id).Select(t => new { t.Id, t.Status, t.Result }).ToListAsync();
        var eventCount = await f.Db.SalesRequestEvents.CountAsync();

        var result = await f.Service.TransitionAsync(request.Id,
            new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, force, null), f.Manager.Id, default);

        Assert.Equal(409, result.Status);
        Assert.NotNull(result.Details);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(before, await f.Db.TaskJournalItems.AsNoTracking().Where(t => t.SalesRequestId == request.Id)
            .OrderBy(t => t.Id).Select(t => new { t.Id, t.Status, t.Result }).ToListAsync());
        Assert.Contains(before, t => t.Id == manual.Id);
        Assert.Equal(eventCount, await f.Db.SalesRequestEvents.CountAsync());
        Assert.Null((await f.Db.SalesRequests.SingleAsync(t => t.Id == request.Id)).PaidPaymentId);
        Assert.False(await f.Db.ClientCases.AnyAsync());
    }

    [Theory]
    [InlineData("no-edit")]
    [InlineData("foreign")]
    [InlineData("inactive")]
    public async Task Paid_RechecksCurrentRequestAccessBeforeReadingPaymentOrTasks(string reason)
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var owner = reason == "foreign" ? f.Admin : f.Manager;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), owner.Id, default)).Value!;
        var payment = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 100m };
        f.Db.Payments.Add(payment);
        if (reason == "no-edit") f.Manager.Role!.Permissions.Single(p => p.Section == RolePermissionCatalog.Requests).CanEdit = false;
        if (reason == "inactive") f.Manager.IsActive = false;
        await f.Db.SaveChangesAsync();
        var eventsBefore = await f.Db.SalesRequestEvents.CountAsync();

        var result = await f.Service.TransitionAsync(request.Id,
            new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, true, null), f.Manager.Id, default);

        Assert.Equal(403, result.Status);
        Assert.Null(result.Details);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(eventsBefore, await f.Db.SalesRequestEvents.CountAsync());
        Assert.Null((await f.Db.SalesRequests.SingleAsync(t => t.Id == request.Id)).PaidPaymentId);
        Assert.False(await f.Db.ClientCases.AnyAsync());
    }

    [Fact]
    public async Task Paid_RejectsTaskReopenedBeforeTransition()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await CloseRequestTasksAsync(f.Db, request.Id);
        var task = await f.Db.TaskJournalItems.FirstAsync(t => t.SalesRequestId == request.Id);
        task.Status = "inProgress";
        task.Result = null;
        await f.Db.SaveChangesAsync();
        var payment = await PreparePaidInvoiceAsync(f, request);

        var result = await f.Service.TransitionAsync(request.Id,
            new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, true, null), f.Manager.Id, default);

        Assert.Equal(409, result.Status);
        f.Db.ChangeTracker.Clear();
        Assert.Equal("inProgress", (await f.Db.TaskJournalItems.SingleAsync(t => t.Id == task.Id)).Status);
        Assert.False(await f.Db.ClientCases.AnyAsync());
    }

    [Fact]
    public async Task Paid_RequiresRealPaymentOfSameClient()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var other = new Client { Name = "Другой", Phone = "+79000000001" };
        f.Db.Clients.Add(other);
        await f.Db.SaveChangesAsync();
        var foreign = new Payment { ClientId = other.Id, Amount = 100m, PaidAmount = 100m, Description = "Чужой" };
        f.Db.Payments.Add(foreign);
        await f.Db.SaveChangesAsync();
        var target = StatusId(f.Db, "paid");

        Assert.False((await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, null, true, null), f.Manager.Id, default)).Ok);
        var paid = await PreparePaidInvoiceAsync(f, request);
        Assert.Equal(404, (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, foreign.Id, true, null), f.Manager.Id, default)).Status);
        var receipt = await f.Db.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == paid.InvoiceId);
        receipt.Amount = 50m;
        await f.Db.SaveChangesAsync();
        Assert.Equal(409, (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, paid.Id, true, null), f.Manager.Id, default)).Status);
        receipt.Amount = 100m;
        await f.Db.SaveChangesAsync();
        await CloseRequestTasksAsync(f.Db, request.Id);
        var done = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(target, request.Version, paid.Id, true, null), f.Manager.Id, default);

        Assert.True(done.Ok);
        Assert.Equal("Success", done.Value!.FinalKind);
        Assert.Equal(paid.Id, done.Value.PaidPaymentId);
    }

    [Fact]
    public async Task Paid_RejectsExpenseWithoutClosingTasksOrCreatingCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var expense = new Payment { ClientId = f.Client.Id, Type = PaymentType.Expense, Amount = 100m, PaidAmount = 100m };
        f.Db.Payments.Add(expense);
        await f.Db.SaveChangesAsync();
        var originalVersion = request.Version;
        var originalStatus = request.StatusId;
        var eventCount = await f.Db.SalesRequestEvents.CountAsync();

        var result = await f.Service.TransitionAsync(request.Id,
            new SalesRequestTransitionInput(StatusId(f.Db, "paid"), originalVersion, expense.Id, true, null), f.Manager.Id, default);

        Assert.Equal(409, result.Status);
        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.SalesRequests.SingleAsync(item => item.Id == request.Id);
        Assert.Equal(originalVersion, stored.Version);
        Assert.Equal(originalStatus, stored.StatusId);
        Assert.Equal(SalesRequestFinalKinds.None, stored.FinalKind);
        Assert.Null(stored.PaidPaymentId);
        Assert.Null(stored.ResultClientCaseId);
        Assert.False(await f.Db.ClientCases.AnyAsync());
        Assert.Equal(eventCount, await f.Db.SalesRequestEvents.CountAsync());
        Assert.All(await f.Db.TaskJournalItems.Where(item => item.SalesRequestId == request.Id).ToListAsync(),
            task => Assert.DoesNotContain(task.Status, new[] { "done", "cancelled" }));
    }

    [Fact]
    public async Task Report_CountsConversionBySource()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var won = (await f.Service.CreateAsync(NewRequest(f.Client, "Avito") with { Amount = 500m }, f.Manager.Id, default)).Value!;
        await f.Service.CreateAsync(NewRequest(f.Client, "Avito"), f.Manager.Id, default);
        var lost = (await f.Service.CreateAsync(NewRequest(f.Client, "Call") with { Amount = 70m }, f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, won, 500m);
        await CloseRequestTasksAsync(f.Db, won.Id);
        await f.Service.TransitionAsync(won.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), won.Version, payment.Id, true, null), f.Manager.Id, default);
        await f.Service.TransitionAsync(lost.Id, new SalesRequestTransitionInput(StatusId(f.Db, "declined"), lost.Version, null, true, null), f.Manager.Id, default);

        var report = await f.Service.BuildReportAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, default);

        Assert.Equal(3, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.InProgress);
        Assert.Equal(33.3m, report.ConversionPercent);
        Assert.Equal(500m, report.WonAmount);
        Assert.Equal(70m, report.LostAmount);
        Assert.Equal(50m, report.BySource.Single(s => s.Source == "Avito").ConversionPercent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Report_RequiresAnalyticsAndPreservesRequestVisibility(bool analytics, bool admin)
    {
        var f = await CreateAsync();
        await using var db = f.Db;
        db.RolePermissions.Single(item => item.RoleId == f.Manager.RoleId).CanViewAnalytics = analytics;
        await db.SaveChangesAsync();
        await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default);
        await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default);
        var controller = ControllerFor(db, admin ? f.Admin : f.Manager, admin);
        var response = await controller.Report(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, default);
        if (!analytics && !admin)
        {
            Assert.IsType<ForbidResult>(response.Result);
            return;
        }
        var report = Assert.IsType<SalesRequestReport>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(admin ? 2 : 1, report.Total);
    }

    private static SalesRequestsController ControllerFor(PaymentContext db, User user, bool admin = false)
        => new(db, new SalesRequestService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, admin ? "admin" : "user")], "Test")),
                },
            },
        };

    [Fact]
    public async Task Controller_DeniesRolesWithoutSection_AndHidesForeignRequestsInOwnScope()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var lawyerRole = new Role { Name = "lawyer", Description = "lawyer", Permissions = { new RolePermission { Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.All } } };
        var lawyer = new User { Email = "l@x.test", FullName = "Юрист", PasswordHash = "x", Role = lawyerRole, IsActive = true, IsApproved = true };
        f.Db.Users.Add(lawyer);
        await f.Db.SaveChangesAsync();
        var mine = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var adminsOwn = (await f.Service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;

        var lawyerList = await ControllerFor(f.Db, lawyer).List(null, null, null, null, null, 1, 30, default);
        var lawyerCreate = await ControllerFor(f.Db, lawyer).Create(new SalesRequestCreateRequest { Source = "Call", ClientId = f.Client.Id, Summary = "x" }, default);
        var managerList = await ControllerFor(f.Db, f.Manager).List(null, null, null, null, null, 1, 30, default);
        var managerForeign = await ControllerFor(f.Db, f.Manager).Get(adminsOwn.Id, default);
        var managerOwn = await ControllerFor(f.Db, f.Manager).Get(mine.Id, default);
        var adminList = await ControllerFor(f.Db, f.Admin, admin: true).List(null, null, null, null, null, 1, 30, default);

        Assert.IsType<ForbidResult>(lawyerList.Result);
        Assert.IsType<ForbidResult>(lawyerCreate.Result);
        var managerJson = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(managerList.Result).Value);
        Assert.Contains($"\"Id\":{mine.Id},", managerJson);
        Assert.DoesNotContain($"\"Id\":{adminsOwn.Id},", managerJson);
        Assert.IsType<NotFoundResult>(managerForeign.Result);
        Assert.IsType<OkObjectResult>(managerOwn.Result);
        Assert.Contains("\"total\":2", System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(adminList.Result).Value));
    }

    [Fact]
    public async Task Paid_CreatesCaseOnceForNewInquiry_AndNotForRequestLinkedToCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var existing = new ClientCase { ClientId = f.Client.Id, Status = ClientCaseStatus.Open, CaseNumber = "5", Title = "Текущее" };
        f.Db.Add(existing);
        await f.Db.SaveChangesAsync();
        var fresh = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var linked = (await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = existing.Id }, f.Manager.Id, default)).Value!;
        var payment = await PreparePaidInvoiceAsync(f, fresh);
        var linkedPayment = await PreparePaidInvoiceAsync(f, linked);
        var paid = StatusId(f.Db, "paid");
        await CloseRequestTasksAsync(f.Db, fresh.Id, linked.Id);

        var casesBefore = await f.Db.ClientCases.CountAsync();
        var done = await f.Service.TransitionAsync(fresh.Id, new SalesRequestTransitionInput(paid, fresh.Version, payment.Id, true, null), f.Manager.Id, default);
        var linkedDone = await f.Service.TransitionAsync(linked.Id, new SalesRequestTransitionInput(paid, linked.Version, linkedPayment.Id, true, null), f.Manager.Id, default);

        Assert.True(done.Ok);
        Assert.NotNull(done.Value!.ResultClientCaseId);
        var created = await f.Db.ClientCases.FindAsync(done.Value.ResultClientCaseId);
        Assert.Equal(f.Client.Id, created!.ClientId);
        Assert.Equal("Спор с застройщиком", created.Title);
        Assert.Equal(casesBefore + 1, await f.Db.ClientCases.CountAsync());
        Assert.Null(linkedDone.Value!.ResultClientCaseId);
        Assert.Contains(f.Db.SalesRequestEvents, e => e.Kind == "CaseCreated" && e.SalesRequestId == fresh.Id);
        var lawyerTask = f.Db.TaskJournalItems.Single(t => t.SalesRequestId == fresh.Id && t.Title.StartsWith("Назначить юриста"));
        Assert.Equal(created.Id, lawyerTask.ClientCaseId);
    }

    [Fact]
    public async Task FailedPaidTransition_DoesNotLeaveCaseBehind()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var payment = new Payment { ClientId = f.Client.Id, Amount = 100m, PaidAmount = 100m, Description = "x" };
        f.Db.Payments.Add(payment);
        await f.Db.SaveChangesAsync();
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE SalesRequests SET Version = Version + 1 WHERE Id = {0}", request.Id);
        var before = await f.Db.ClientCases.CountAsync();

        var stale = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), request.Version, payment.Id, true, null), f.Manager.Id, default);

        Assert.Equal(409, stale.Status);
        Assert.Equal(before, await f.Db.ClientCases.CountAsync());
    }

    [Fact]
    public async Task ReplaceRules_ValidatesAndAffectsOnlyFutureTransitions()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var qualification = StatusId(f.Db, "qualification");

        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput(" ", null, 10)], default)).Ok);
        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить", null, 0)], default)).Ok);
        Assert.Equal(404, (await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить", 9999, 10)], default)).Status);
        var ok = await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("Позвонить клиенту", f.Admin.Id, 15), new SalesRequestRuleInput("Проверить документы", null, 60)], default);

        Assert.True(ok.Ok);
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(qualification, request.Version, null, false, null), f.Manager.Id, default);
        var tasks = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id && t.Title.Contains("Позвонить") || t.Title.Contains("Проверить")).ToList();
        Assert.Equal(f.Admin.Id, tasks.Single(t => t.Title.StartsWith("Позвонить")).AssigneeUserId);
        Assert.Equal(f.Manager.Id, tasks.Single(t => t.Title.StartsWith("Проверить")).AssigneeUserId);
    }

    [Fact]
    public async Task SaveStatus_CreatesRenamesAndProtectsFunnelInvariants()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);

        var created = await f.Service.SaveStatusAsync(null, "Ожидание документов", null, null, null, default);
        var renamed = await f.Service.SaveStatusAsync(created.Value!.Id, "Ждём документы", null, null, null, default);
        var changeFinal = await f.Service.SaveStatusAsync(StatusId(f.Db, "paid"), null, "Failure", null, null, default);
        var blank = await f.Service.SaveStatusAsync(null, " ", null, null, null, default);
        var paid = StatusId(f.Db, "paid");
        var disablePaid = await f.Service.SaveStatusAsync(paid, null, null, null, false, default);
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var disableNew = await f.Service.SaveStatusAsync(request.StatusId, null, null, null, false, default);

        Assert.Equal("Ждём документы", renamed.Value!.Title);
        Assert.Equal(409, changeFinal.Status);
        Assert.False(blank.Ok);
        Assert.Equal(409, disablePaid.Status);
        Assert.Equal(409, disableNew.Status);
    }

    [Fact]
    public async Task AdvanceCompleted_MovesRequestOnlyWhenAllStageTasksAreDone()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "documents-prep"), request.Version, null, false, null), f.Manager.Id, default);
        var stage = f.Db.TaskJournalItems.Single(t => t.SalesRequestId == request.Id && t.Title.StartsWith("Подготовить счёт"));

        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));

        stage.Status = "cancelled";
        await f.Db.SaveChangesAsync();
        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));

        stage.Status = "done";
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Service.AdvanceCompletedAsync(default));

        var moved = await f.Db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Equal(StatusId(f.Db, "documents-sent"), moved.StatusId);
        Assert.Contains(f.Db.TaskJournalItems, t => t.SalesRequestId == request.Id && t.Title.StartsWith("Отправить документы") && t.Status == "new");
        Assert.Equal(0, await f.Service.AdvanceCompletedAsync(default));
    }

    [Fact]
    public async Task SaveStatus_ConfiguresAndValidatesAutoAdvance()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var calc = StatusId(f.Db, "calculation");

        var self = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, calc);
        var fromFinal = await f.Service.SaveStatusAsync(StatusId(f.Db, "paid"), null, null, null, null, default, calc);
        var ok = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, StatusId(f.Db, "approval"));
        var configured = ok.Value!.AutoAdvanceToStatusId;
        Assert.False((await f.Service.SaveStatusAsync(StatusId(f.Db, "approval"), null, null, null, null, default, calc)).Ok);
        Assert.False((await f.Service.SaveStatusAsync(calc, null, null, null, null, default, StatusId(f.Db, "paid"))).Ok);
        var cleared = await f.Service.SaveStatusAsync(calc, null, null, null, null, default, clearAutoAdvance: true);

        Assert.False(self.Ok);
        Assert.False(fromFinal.Ok);
        Assert.Equal(StatusId(f.Db, "approval"), configured);
        Assert.Null(cleared.Value!.AutoAdvanceToStatusId);
    }

    public class NotificationRecorder : System.Reflection.DispatchProxy
    {
        public List<(int TaskId, int RecipientAssignee, int Actor)> Assigned { get; } = [];
        public bool Fail { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(INotificationService.NotifyTaskAssignedAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            if (Fail)
            {
                throw new InvalidOperationException("push unavailable");
            }

            var task = (TaskJournalItem)args![0]!;
            Assigned.Add((task.Id, task.AssigneeUserId ?? 0, (int)args[2]!));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }
    }

    [Fact]
    public async Task AutoTasks_NotifyAssignee_AndNotificationFailureDoesNotBreakTheFunnel()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var proxy = System.Reflection.DispatchProxy.Create<INotificationService, NotificationRecorder>();
        var recorder = (NotificationRecorder)(object)proxy;
        var service = new SalesRequestService(f.Db, proxy);

        var request = (await service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        Assert.Single(recorder.Assigned);
        Assert.Equal(f.Admin.Id, recorder.Assigned[0].RecipientAssignee);
        Assert.Contains("inApp", f.Db.TaskJournalItems.Single(t => t.Id == recorder.Assigned[0].TaskId).ChannelsJson);

        recorder.Fail = true;
        var moved = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), request.Version, null, false, null), f.Manager.Id, default);

        Assert.True(moved.Ok);
        Assert.Contains(f.Db.TaskJournalItems, t => t.SalesRequestId == request.Id && t.Title.StartsWith("Квалифицировать"));
    }

    [Fact]
    public async Task RuleConditions_AmountSourceAndTypeDecideWhetherTheTaskIsCreated()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var qualification = StatusId(f.Db, "qualification");
        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("x", null, 5, null, "Радио", null)], default)).Ok);
        Assert.False((await f.Service.ReplaceRulesAsync(qualification, [new SalesRequestRuleInput("x", null, 5, -1m, null, null)], default)).Ok);
        await f.Service.ReplaceRulesAsync(qualification,
        [
            new SalesRequestRuleInput("Крупная заявка", f.Admin.Id, 30, 50_000m, null, null),
            new SalesRequestRuleInput("Только Авито", null, 30, null, "Avito", null),
            new SalesRequestRuleInput("Только фиксированный", null, 30, null, null, "Fixed"),
            new SalesRequestRuleInput("Всегда", null, 30),
        ], default);
        var small = (await f.Service.CreateAsync(NewRequest(f.Client, "Call") with { Amount = 1_000m }, f.Manager.Id, default)).Value!;
        var big = (await f.Service.CreateAsync(NewRequest(f.Client, "Avito") with { Amount = 90_000m }, f.Manager.Id, default)).Value!;

        await f.Service.TransitionAsync(small.Id, new SalesRequestTransitionInput(qualification, small.Version, null, false, null), f.Manager.Id, default);
        await f.Service.TransitionAsync(big.Id, new SalesRequestTransitionInput(qualification, big.Version, null, false, null), f.Manager.Id, default);

        var smallTitles = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == small.Id).Select(t => t.Title).ToList();
        var bigTitles = f.Db.TaskJournalItems.Where(t => t.SalesRequestId == big.Id).Select(t => t.Title).ToList();
        Assert.Contains(smallTitles, t => t.StartsWith("Всегда"));
        Assert.DoesNotContain(smallTitles, t => t.StartsWith("Крупная") || t.StartsWith("Только"));
        Assert.Contains(bigTitles, t => t.StartsWith("Крупная"));
        Assert.Contains(bigTitles, t => t.StartsWith("Только Авито"));
        Assert.DoesNotContain(bigTitles, t => t.StartsWith("Только фиксированный"));
    }

    [Fact]
    public async Task Sla_MarksBreachOncePerStageEntry_AndLossReportGroupsByStage()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Service.EnsureDefaultsAsync(default);
        var qualification = StatusId(f.Db, "qualification");
        Assert.False((await f.Service.SetSlaAsync(StatusId(f.Db, "paid"), 30, default)).Ok);
        Assert.False((await f.Service.SetSlaAsync(qualification, 0, default)).Ok);
        Assert.True((await f.Service.SetSlaAsync(qualification, 60, default)).Ok);
        var request = (await f.Service.CreateAsync(NewRequest(f.Client) with { Amount = 700m }, f.Manager.Id, default)).Value!;
        var moved = (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(qualification, request.Version, null, false, null), f.Manager.Id, default)).Value!;

        Assert.Equal(0, await f.Service.MarkSlaBreachesAsync(default));
        moved.StatusEnteredAt = DateTime.UtcNow.AddMinutes(-90);
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, await f.Service.MarkSlaBreachesAsync(default));
        Assert.Equal(0, await f.Service.MarkSlaBreachesAsync(default));
        Assert.Single(f.Db.SalesRequestEvents.Where(e => e.Kind == "SlaBreached"));

        var failed = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "declined"), moved.Version, null, true, null), f.Manager.Id, default);
        var report = await f.Service.BuildReportAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, default);

        Assert.True(failed.Ok);
        var stage = Assert.Single(report.LossByStage);
        Assert.Equal("Квалификация", stage.Stage);
        Assert.Equal(700m, stage.LostAmount);
    }

    [Fact]
    public async Task Outbox_SurvivesNotificationFailure_RetriesWithBackoff_AndDeliversExactlyOnce()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var proxy = System.Reflection.DispatchProxy.Create<INotificationService, NotificationRecorder>();
        var recorder = (NotificationRecorder)(object)proxy;
        var service = new SalesRequestService(f.Db, proxy);
        recorder.Fail = true;

        var request = (await service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;

        var row = Assert.Single(f.Db.SalesRequestNotificationOutbox.AsNoTracking());
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.DeliveredAt);
        Assert.True(row.NextAttemptAt > DateTime.UtcNow);
        Assert.Contains("push unavailable", row.LastError);
        Assert.Empty(recorder.Assigned);
        Assert.Equal(0, await service.DeliverPendingNotificationsAsync(default));

        await f.Db.SalesRequestNotificationOutbox.ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        recorder.Fail = false;
        Assert.Equal(1, await service.DeliverPendingNotificationsAsync(default));
        Assert.Equal(0, await service.DeliverPendingNotificationsAsync(default));

        Assert.Single(recorder.Assigned);
        Assert.NotNull((await f.Db.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync()).DeliveredAt);
        Assert.Equal($"task:{recorder.Assigned[0].TaskId}:assigned", row.Key);
        Assert.True(request.Id > 0);
    }

    [Fact]
    public async Task Outbox_IsRolledBackWithTheTransaction_AndClosedTasksAreAbandoned()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var proxy = System.Reflection.DispatchProxy.Create<INotificationService, NotificationRecorder>();
        var recorder = (NotificationRecorder)(object)proxy;
        var service = new SalesRequestService(f.Db, proxy);
        var request = (await service.CreateAsync(NewRequest(f.Client), f.Admin.Id, default)).Value!;
        var before = await f.Db.SalesRequestNotificationOutbox.CountAsync();

        var stale = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "qualification"), request.Version + 5, null, false, null), f.Manager.Id, default);

        Assert.Equal(409, stale.Status);
        Assert.Equal(before, await f.Db.SalesRequestNotificationOutbox.CountAsync());
        await f.Db.SalesRequestNotificationOutbox.ExecuteUpdateAsync(s => s.SetProperty(r => r.DeliveredAt, (DateTime?)null).SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        await f.Db.TaskJournalItems.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "cancelled"));

        Assert.Equal(0, await service.DeliverPendingNotificationsAsync(default));
        Assert.NotNull((await f.Db.SalesRequestNotificationOutbox.AsNoTracking().FirstAsync()).AbandonedAt);
    }

    [Fact]
    public async Task AutoAdvance_IsRecheckedUnderTheLock_AndNeverLeadsToAFinalStatus()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client), f.Manager.Id, default)).Value!;
        var prep = StatusId(f.Db, "documents-prep");
        var moved = (await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(prep, request.Version, null, false, null), f.Manager.Id, default)).Value!;
        var stage = f.Db.TaskJournalItems.Single(t => t.SalesRequestId == request.Id && t.Title.StartsWith("Подготовить счёт"));
        var sent = StatusId(f.Db, "documents-sent");

        // Задача ещё открыта (например, переоткрыта между чтением и переходом): автопереход не выполняется.
        var tooEarly = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(sent, moved.Version, null, false, "auto", prep), 0, default);
        stage.Status = "done";
        await f.Db.SaveChangesAsync();
        var toFinal = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(StatusId(f.Db, "paid"), moved.Version, null, true, "auto", prep), 0, default);
        var wrongStage = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(sent, moved.Version, null, false, "auto", StatusId(f.Db, "approval")), 0, default);
        var ok = await f.Service.TransitionAsync(request.Id, new SalesRequestTransitionInput(sent, moved.Version, null, false, "auto", prep), 0, default);

        Assert.Equal(409, tooEarly.Status);
        Assert.Equal(409, toFinal.Status);
        Assert.Equal(409, wrongStage.Status);
        Assert.True(ok.Ok);
        var saveToFinal = await f.Service.SaveStatusAsync(prep, null, null, null, null, default, StatusId(f.Db, "declined"));
        Assert.False(saveToFinal.Ok);
    }

    private async Task<ClientCase> AddCaseWithContractAsync(Fixture f, string? billingMode, string contractStatus = ContractStatus.Concluded, ClientCaseStatus caseStatus = ClientCaseStatus.Open)
    {
        var clientCase = new ClientCase { ClientId = f.Client.Id, Status = caseStatus, CaseNumber = "N" + Guid.NewGuid().ToString("N")[..4], Title = "Дело", ResponsibleUserId = f.Manager.Id };
        f.Db.ClientCases.Add(clientCase);
        await f.Db.SaveChangesAsync();
        if (billingMode is not null)
        {
            var contract = new Contract { Number = "К-" + clientCase.Id, BillingMode = billingMode, Status = contractStatus, Title = "Договор" };
            f.Db.Contracts.Add(contract);
            await f.Db.SaveChangesAsync();
            f.Db.ClientCaseContracts.Add(new ClientCaseContract { ClientCaseId = clientCase.Id, ContractId = contract.Id });
            await f.Db.SaveChangesAsync();
        }

        return clientCase;
    }

    [Fact]
    public async Task AutoType_ComesOnlyFromTheActualConcludedContractOfTheCase()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var fixedCase = await AddCaseWithContractAsync(f, ContractBillingMode.Fixed);
        var actualCase = await AddCaseWithContractAsync(f, ContractBillingMode.Actual);
        var draftContractCase = await AddCaseWithContractAsync(f, ContractBillingMode.Fixed, ContractStatus.Draft);
        var noContractCase = await AddCaseWithContractAsync(f, null);
        var closedFixedCase = await AddCaseWithContractAsync(f, ContractBillingMode.Fixed, caseStatus: ClientCaseStatus.Closed);

        async Task<SalesRequest> For(ClientCase c) => (await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = c.Id }, f.Manager.Id, default)).Value!;

        Assert.Equal("Fixed", (await For(fixedCase)).Type);
        Assert.Equal("PerCase", (await For(actualCase)).Type);
        var draft = await For(draftContractCase);
        var none = await For(noContractCase);
        var closed = await For(closedFixedCase);
        Assert.Equal("PerCase", draft.Type);
        Assert.Contains("уточнит квалификатор", draft.Comment);
        Assert.Equal("PerCase", none.Type);
        Assert.Contains("уточнит квалификатор", none.Comment);
        Assert.Equal("PerCase", closed.Type);
        Assert.Contains("По закрытому делу", closed.Comment);
        var overridden = (await f.Service.CreateAsync(NewRequest(f.Client) with { ClientCaseId = fixedCase.Id, Type = "New" }, f.Manager.Id, default)).Value!;
        Assert.Equal("New", overridden.Type);
    }

    [Fact]
    public async Task Update_AllowsChangingSource_RejectsUnknownOne_AndKeepsVersionConflictProtection()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var request = (await f.Service.CreateAsync(NewRequest(f.Client, "Call"), f.Manager.Id, default)).Value!;

        var bad = await f.Service.UpdateAsync(request.Id, new SalesRequestUpdateInput(null, null, null, null, null, request.Version, "Радио"), f.Manager.Id, default);
        var ok = await f.Service.UpdateAsync(request.Id, new SalesRequestUpdateInput(null, null, null, null, null, request.Version, "Avito"), f.Manager.Id, default);
        var stale = await f.Service.UpdateAsync(request.Id, new SalesRequestUpdateInput("Новая суть", null, null, null, null, request.Version - 1 < 0 ? 99 : request.Version - 1, null), f.Manager.Id, default);

        Assert.False(bad.Ok);
        Assert.Equal("Avito", ok.Value!.Source);
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task ClientCasesEndpoint_PagesCasesWithLawyerContractAndLastActivity_AndSearchDoesNotLoadCases()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        for (var i = 0; i < 25; i++)
        {
            await AddCaseWithContractAsync(f, i == 0 ? ContractBillingMode.Fixed : null);
        }

        var controller = ControllerFor(f.Db, f.Admin, admin: true);
        var first = Assert.IsType<OkObjectResult>((await controller.ClientCases(f.Client.Id, 0, 20, default)).Result);
        var firstPage = Assert.IsType<SalesRequestClientCasePageDto>(first.Value);
        var second = Assert.IsType<SalesRequestClientCasePageDto>(Assert.IsType<OkObjectResult>((await controller.ClientCases(f.Client.Id, 20, 20, default)).Result).Value);
        var search = Assert.IsType<List<SalesRequestClientDto>>(Assert.IsType<OkObjectResult>((await controller.SearchClients("Иванов", default)).Result).Value);

        Assert.Equal(20, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Equal(25, firstPage.Items.Count + second.Items.Count);
        Assert.Equal("Аня", firstPage.Items[0].LawyerName);
        Assert.Contains(second.Items, item => item.ContractMode == "Fixed");
        Assert.All(firstPage.Items, item => Assert.NotNull(item.LastActivityAt));
        Assert.Equal(25, Assert.Single(search).CasesCount);
    }
}
