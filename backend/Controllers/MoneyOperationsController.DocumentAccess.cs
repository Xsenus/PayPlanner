using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Banking;

namespace PayPlanner.Api.Controllers;

public partial class MoneyOperationsController
{
    private SectionAccessContext? moneyContractReadAccess;
    private SectionAccessContext? moneyActReadAccess;

    private IQueryable<Contract> ReadableMoneyContracts()
        => moneyAccess is null || moneyContractReadAccess?.Permission?.CanView != true
            ? _db.Contracts.AsNoTracking().Where(_ => false)
            : BankStatementImportAccess.ScopeKnownDocumentCompanies(_db,
                _db.Contracts.AsNoTracking().ApplyContractVisibility(moneyContractReadAccess.Visibility), moneyAccess.AllowedCompanyIds);

    private IQueryable<Act> ReadableMoneyActs()
        => moneyAccess is null || moneyActReadAccess?.Permission?.CanView != true
            ? _db.Acts.AsNoTracking().Where(_ => false)
            : BankStatementImportAccess.ScopeKnownDocumentCompanies(_db,
                _db.Acts.AsNoTracking().ApplyActVisibility(moneyActReadAccess.Visibility), moneyAccess.AllowedCompanyIds);
}
