using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

public partial class MoneyOperationsController
{
    private sealed record MoneyInvoiceAccess(SectionAccessContext Section, int CompanyId);
    private SectionAccessContext? moneyInvoiceReadAccess;

    private IQueryable<MoneyOperation> ReadableMoneyInvoiceOperations()
    {
        var operations = _db.MoneyOperations.AsNoTracking();
        if (moneyInvoiceReadAccess is null || moneyAccess is null) return operations.Where(_ => false);
        var headers = InvoicePaymentAccess.ScopeHeaders(_db, moneyInvoiceReadAccess);
        var accounts = CompanyMoneyAccounts(moneyAccess, forNewOperations: false);
        return operations.Where(operation => operation.LinkedInvoiceId.HasValue && operation.MoneyAccountId.HasValue
            && (operation.Direction == MoneyOperationDirection.Income || operation.Direction == MoneyOperationDirection.Expense)
            && accounts.Any(account => account.Id == operation.MoneyAccountId && account.CompanyId.HasValue
                && headers.Any(header => header.InvoiceId == operation.LinkedInvoiceId && header.SellerCompanyId == account.CompanyId
                    && header.Type == (operation.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income))));
    }

    private async Task<MoneyInvoiceAccess?> ResolveMoneyInvoiceAccessAsync(MoneyOperation operation, bool forWrite, CancellationToken ct)
    {
        if (!operation.MoneyAccountId.HasValue) return null;
        var money = await ResolveMoneyAccessAsync(ct);
        if (money is null || !money.Permission.CanView) return null;
        var companyId = await CompanyMoneyAccounts(money, forNewOperations: false)
            .Where(account => account.Id == operation.MoneyAccountId.Value)
            .Select(account => account.CompanyId).SingleOrDefaultAsync(ct);
        if (!companyId.HasValue) return null;
        var accounts = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (accounts?.Permission?.CanView != true || forWrite && !accounts.Permission.CanEdit) return null;
        return new(accounts, companyId.Value);
    }

    private async Task<bool> CanAccessMoneyInvoiceAsync(MoneyOperation operation, int invoiceId, bool forWrite, CancellationToken ct)
    {
        var access = await ResolveMoneyInvoiceAccessAsync(operation, forWrite, ct);
        if (access is null) return false;
        var paymentType = operation.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income;
        return await InvoicePaymentAccess.ScopeHeaders(_db, access.Section, access.CompanyId)
            .AnyAsync(payment => payment.InvoiceId == invoiceId && payment.Type == paymentType, ct);
    }
}
