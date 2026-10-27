using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestFinalConcurrencyTests(PostgreSqlModelTemplate databases) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceInvoice_RejectsConcurrentContractVisibilityChange(bool useCompany)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.Own },
            },
        };
        var user = new User { Email = "sales-bind-contract@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент заявки", Phone = "+79000000015" };
        var visibleClient = new Client { Name = "Клиент договора", Phone = "+79000000016", ResponsibleUser = user };
        var company = new LegalEntity { ShortName = "Компания", ResponsibleUser = user };
        var contract = new Contract { Number = "ДОГ-ТЕСТ", Date = new DateTime(2026, 1, 1) };
        db.AddRange(user, client, visibleClient, company, contract);
        await db.SaveChangesAsync();
        if (useCompany) db.LegalEntityContracts.Add(new LegalEntityContract { LegalEntityId = company.Id, ContractId = contract.Id });
        else db.ClientContracts.Add(new ClientContract { ClientId = visibleClient.Id, ContractId = contract.Id });
        var invoice = new Invoice { Number = "СЧ-ДОГОВОР", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ContractId = contract.Id, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (useCompany)
            await concurrent.LegalEntityContracts.Where(link => link.ContractId == contract.Id && link.LegalEntityId == company.Id).ExecuteDeleteAsync();
        else
            await concurrent.ClientContracts.Where(link => link.ContractId == contract.Id && link.ClientId == visibleClient.Id).ExecuteDeleteAsync();
        var blocked = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default, 40m);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default, 40m);
        Assert.Equal(404, denied.Status);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ServiceInvoiceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServiceInvoice_RejectsConcurrentInvoiceVisibilityChange(bool useClientOwner)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.Own },
            },
        };
        var user = new User { Email = "sales-bind-visibility@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000014", ResponsibleUser = useClientOwner ? user : null };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        var invoice = new Invoice { Number = "СЧ-ВИДИМОСТЬ", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        if (!useClientOwner) db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = payment.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (useClientOwner)
            await concurrent.Clients.Where(item => item.Id == client.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ResponsibleUserId, (int?)null));
        else
            await concurrent.PaymentResponsibles.Where(item => item.PaymentId == payment.Id && item.UserId == user.Id).ExecuteDeleteAsync();
        var blocked = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default, 40m);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default, 40m);
        Assert.Equal(404, denied.Status);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ServiceInvoiceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulTransition_RejectsConcurrentClientVisibilityChange(bool useClientOwner)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
        {
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
            new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.Own },
            new RolePermission { Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All },
        }
        };
        var user = new User { Email = "sales-client-visibility@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true, IsEmployee = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000006", ResponsibleUser = useClientOwner ? user : null };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = company.Id });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var invoice = new Invoice { Number = "СЧ-КЛИЕНТ", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 100m };
        db.AddRange(account, payment);
        await db.SaveChangesAsync();
        if (!useClientOwner) db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = payment.Id, UserId = user.Id });
        db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m, ResponsibleUserId = user.Id });
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await db.TaskJournalItems.Where(task => task.SalesRequestId == request.Id).ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, "done"));
        var binding = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var version = binding.Value!.Version;
        var paid = await db.SalesRequestStatuses.Where(status => status.Key == "paid").Select(status => status.Id).SingleAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (useClientOwner)
            await concurrent.Clients.Where(item => item.Id == client.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ResponsibleUserId, (int?)null));
        else
            await concurrent.PaymentResponsibles.Where(item => item.PaymentId == payment.Id && item.UserId == user.Id).ExecuteDeleteAsync();
        var blocked = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.False(denied.Ok);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceInvoice_ConcurrentRequestsCannotOverallocateOrShareExclusiveInvoice(bool shared)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var user = new User { Email = "sales-invoice-race@example.test", FullName = "Тест", PasswordHash = "x", Role = new Role { Name = "admin", Description = "admin" }, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000007" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        var invoice = new Invoice { Number = "СЧ-ОБЩИЙ", Date = new DateTime(2026, 1, 1) };
        db.Payments.Add(new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var first = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Первое", null, null, null), user.Id, default)).Value!;
        var second = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Второе", null, null, null), user.Id, default)).Value!;
        await using var firstDb = new PostgreSqlPaymentContext(options);
        await using var secondDb = new PostgreSqlPaymentContext(options);
        var results = await Task.WhenAll(
            new SalesRequestService(firstDb).SetServiceInvoiceAsync(first.Id, first.Version, invoice.Id, user.Id, default, shared ? 70m : null),
            new SalesRequestService(secondDb).SetServiceInvoiceAsync(second.Id, second.Version, invoice.Id, user.Id, default, shared ? 70m : null));
        Assert.Single(results, result => result.Ok);
        Assert.Single(results, result => result.Status == 409);
        Assert.Equal(1, await db.SalesRequests.AsNoTracking().CountAsync(item => item.ServiceInvoiceId == invoice.Id));
        if (shared)
        {
            var remaining = results[0].Ok ? second : first;
            await using var retryDb = new PostgreSqlPaymentContext(options);
            var retry = await new SalesRequestService(retryDb).SetServiceInvoiceAsync(remaining.Id, remaining.Version, invoice.Id, user.Id, default, 30m);
            Assert.True(retry.Ok, retry.Error);
            Assert.Equal(2, await db.SalesRequests.AsNoTracking().CountAsync(item => item.ServiceInvoiceId == invoice.Id));
        }
    }

    [Theory]
    [InlineData(RolePermissionCatalog.Accounts)]
    [InlineData(RolePermissionCatalog.Payments)]
    [InlineData("company-access")]
    public async Task SuccessfulTransition_RejectsConcurrentFinancialPermissionRevocation(string section)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
        {
            new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
            new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All },
        }
        };
        var user = new User { Email = "sales-financial-permission@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true, IsEmployee = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000008" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = company.Id });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var invoice = new Invoice { Number = "СЧ-ПРАВА", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 100m };
        db.AddRange(account, payment);
        await db.SaveChangesAsync();
        db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m, ResponsibleUserId = user.Id });
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await db.TaskJournalItems.Where(task => task.SalesRequestId == request.Id).ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, "done"));
        var binding = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var version = binding.Value!.Version;
        var paid = await db.SalesRequestStatuses.Where(status => status.Key == "paid").Select(status => status.Id).SingleAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (section == "company-access")
            await concurrent.UserCompanyAccesses.Where(access => access.UserId == user.Id && access.LegalEntityId == company.Id).ExecuteDeleteAsync();
        else
            await concurrent.RolePermissions.Where(permission => permission.RoleId == role.Id && permission.Section == section)
                .ExecuteUpdateAsync(setters => setters.SetProperty(permission => permission.CanView, false));
        var blocked = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.False(denied.Ok);
        Assert.Null(denied.Details);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("invoice")]
    [InlineData("deleted")]
    public async Task SuccessfulTransition_RejectsConcurrentReceiptOrInvoiceChange(string scenario)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var user = new User { Email = "sales-financial-race@example.test", FullName = "Тест", PasswordHash = "x", Role = new Role { Name = "admin", Description = "admin" }, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000009" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var invoice = new Invoice { Number = "СЧ-ФИНАНСЫ", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 100m };
        db.AddRange(account, payment);
        await db.SaveChangesAsync();
        var operation = new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m, ResponsibleUserId = user.Id };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await db.TaskJournalItems.Where(task => task.SalesRequestId == request.Id).ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, "done"));
        var binding = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var version = binding.Value!.Version;
        var paid = await db.SalesRequestStatuses.Where(status => status.Key == "paid").Select(status => status.Id).SingleAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (scenario == "invoice")
            await concurrent.Payments.Where(item => item.Id == payment.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Amount, 150m));
        else if (scenario == "deleted")
            await concurrent.MoneyOperations.Where(item => item.Id == operation.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => EF.Property<bool>(item, "IsDeleted"), true));
        else
            await concurrent.MoneyOperations.Where(item => item.Id == operation.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Amount, 50m));
        var blocked = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, version, payment.Id, false, null), user.Id, default);
        Assert.Equal(409, denied.Status);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }

    [Fact]
    public async Task ServiceInvoice_RejectsConcurrentPreviousInvoiceChange()
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All },
            },
        };
        var user = new User { Email = "sales-old-bind@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Тест", Phone = "+79000000003" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        var oldInvoice = new Invoice { Number = "ТЕСТ-СТАРЫЙ", Date = new DateTime(2026, 1, 1) };
        var newInvoice = new Invoice { Number = "ТЕСТ-НОВЫЙ", Date = new DateTime(2026, 1, 1) };
        var oldPayment = new Payment { Invoice = oldInvoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        db.Payments.AddRange(oldPayment, new Payment { Invoice = newInvoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await db.SaveChangesAsync();
        var bound = await service.SetServiceInvoiceAsync(request.Id, request.Version, oldInvoice.Id, user.Id, default);
        Assert.True(bound.Ok, bound.Error);
        var version = bound.Value!.Version;
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        await concurrent.Payments.Where(p => p.Id == oldPayment.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Cancelled));
        var blocked = await service.SetServiceInvoiceAsync(request.Id, version, newInvoice.Id, user.Id, default);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.SetServiceInvoiceAsync(request.Id, version, newInvoice.Id, user.Id, default);
        Assert.Equal(404, denied.Status);
        Assert.Equal(oldInvoice.Id, (await db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceInvoice_RejectsConcurrentInvoiceOrAccountPermissionChange(bool revokeAccess)
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All },
            },
        };
        var user = new User { Email = "sales-bind@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Тест", Phone = "+79000000002" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(user, client, company);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        var invoice = new Invoice { Number = "ТЕСТ-1", Date = new DateTime(2026, 1, 1) };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var beforeEvents = await db.SalesRequestEvents.CountAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (revokeAccess)
            await concurrent.RolePermissions.Where(p => p.RoleId == role.Id && p.Section == RolePermissionCatalog.Accounts)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanView, false));
        else
            await concurrent.Payments.Where(p => p.Id == payment.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Description, "Изменение"));

        var blocked = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        Assert.Equal(409, blocked.Status);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).ServiceInvoiceId);
        Assert.Equal(beforeEvents, await db.SalesRequestEvents.CountAsync());
        var result = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        if (revokeAccess)
        {
            Assert.Equal(404, result.Status);
            Assert.Null(result.Details);
        }
        else
        {
            Assert.True(result.Ok, result.Error);
            Assert.Equal(invoice.Id, result.Value!.ServiceInvoiceId);
        }
    }

    [Fact]
    public async Task SuccessfulTransition_RejectsConcurrentPermissionRevocation()
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role
        {
            Name = "manager",
            Description = "manager",
            Permissions = { new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own } },
        };
        var user = new User { Email = "sales-revocation@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Тест", Phone = "+79000000001" };
        db.AddRange(user, client);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default)).Value!;
        await db.TaskJournalItems.Where(t => t.SalesRequestId == request.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "done"));
        var payment = new Payment { ClientId = client.Id, Amount = 100m, PaidAmount = 100m };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var paid = await db.SalesRequestStatuses.Where(s => s.Key == "paid").Select(s => s.Id).SingleAsync();
        var beforeEvents = await db.SalesRequestEvents.CountAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        await concurrent.RolePermissions.Where(p => p.RoleId == role.Id && p.Section == RolePermissionCatalog.Requests)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        var blocked = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, payment.Id, false, null), user.Id, default);
        Assert.Equal(409, blocked.Status);
        Assert.Contains("сейчас изменяются", blocked.Error);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var denied = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, payment.Id, false, null), user.Id, default);
        Assert.Equal(403, denied.Status);
        Assert.Null(denied.Details);
        Assert.Equal(beforeEvents, await db.SalesRequestEvents.CountAsync());
        Assert.False(await db.ClientCases.AnyAsync());
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).PaidPaymentId);
    }

    [Fact]
    public async Task SuccessfulTransition_RejectsLockedTaskAndSucceedsOnlyAfterExplicitClosure()
    {
        await using var lease = await databases.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options);
        var role = new Role { Name = "admin", Description = "admin" };
        var user = new User { Email = "sales-lock@example.test", FullName = "Тест", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Тест", Phone = "+79000000000" };
        db.AddRange(user, client);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var created = await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), user.Id, default);
        Assert.True(created.Ok);
        var request = created.Value!;
        var task = await db.TaskJournalItems.AsNoTracking().FirstAsync(t => t.SalesRequestId == request.Id);
        var company = new LegalEntity { ShortName = "Компания" };
        var invoice = new Invoice { Number = "СЧ-ЗАДАЧА", Date = new DateTime(2026, 1, 1) };
        db.AddRange(company, invoice);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m, PaidAmount = 0m };
        db.AddRange(account, payment);
        await db.SaveChangesAsync();
        db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m, ResponsibleUserId = user.Id });
        await db.SaveChangesAsync();
        var binding = await service.SetServiceInvoiceAsync(request.Id, request.Version, invoice.Id, user.Id, default);
        Assert.True(binding.Ok, binding.Error);
        var paid = await db.SalesRequestStatuses.Where(s => s.Key == "paid").Select(s => s.Id).SingleAsync();
        await using var concurrent = new PostgreSqlPaymentContext(options);
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"TaskJournalItems\" SET \"Status\" = 'done' WHERE \"Id\" = {task.Id}");
        var blocked = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, payment.Id, true, null), user.Id, default);
        Assert.Equal(409, blocked.Status);
        Assert.Contains("сейчас изменяются", blocked.Error);
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("done", (await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
        var result = await service.TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, payment.Id, false, null), user.Id, default);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(SalesRequestFinalKinds.Success, result.Value!.FinalKind);
    }
}
