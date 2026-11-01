using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

public sealed record CompanyBankDocumentSelection(int BindingId, int BankAccountId, int? MoneyAccountId,
    string Provider, string ExternalAccountId, string Currency);

/// <summary>Checks a new seller selection within its company/branch lease; never repairs financial identities.</summary>
public sealed class CompanyBankDocumentSelectionService(PaymentContext db)
{
    public async Task<CompanyBankDocumentSelection?> ValidateAsync(LegalEntity company, CompanyBranch? branch, CancellationToken ct, string? requiredCurrency = null)
    {
        if (db.Database.CurrentTransaction is null) throw new SellerSnapshotConflictException();
        var bindings = await db.CompanyBankAccountBindings.AsNoTracking()
            .Where(x => x.LegalEntityId == company.Id && x.RevokedAtUtc == null)
            .OrderBy(x => x.Id).Select(x => new
            {
                x.Id,
                x.ProfileAccountKey,
                ProfileId = x.ProfileAccountId.Length <= CompanyBankAccountList.MaxJsonLength ? x.ProfileAccountId : null,
                x.BankConnectionAccountId,
                x.Provider,
                x.ExternalAccountId,
                x.AccountNumber,
                x.Bik,
                x.Currency
            }).Take(CompanyBankAccountMatchService.MaximumProfileAccounts + 1).ToArrayAsync(ct);
        // Manual historical accounts are not matched by number, name or a JSON bank ID.
        if (bindings.Length == 0) return null;
        if (bindings.Length > CompanyBankAccountMatchService.MaximumProfileAccounts || company.BankAccountsJson?.Length > CompanyBankAccountList.MaxJsonLength)
            throw Unavailable();
        var issues = new List<string>();
        CompanyBankAccountMatchService.Profile[] profiles;
        try { profiles = CompanyBankAccountMatchService.ReadProfiles(company.BankAccountsJson, issues); }
        catch (CompanyBankAccountMatchLimitException) { throw Unavailable(); }
        if (issues.Any(x => x is "InvalidProfileAccounts" or "DuplicateProfileId")) throw Unavailable();
        var branchId = string.IsNullOrWhiteSpace(branch?.BankAccountId) ? null : branch.BankAccountId.Trim();
        var branchActive = branch is not null && CompanyRequisitesResolver.TryFindActiveAccount(company, branchId, out _);
        if (!branchActive && branchId is not null && bindings.Any(x => x.ProfileId == branchId))
            throw Unavailable(); // Do not silently fall back from an unavailable confirmed branch account.
        var account = branchActive && CompanyRequisitesResolver.TryFindActiveAccount(company, branchId, out var selected)
            ? selected : CompanyBankAccountList.FindPrimaryActive(company.BankAccountsJson);
        if (account is null) throw Unavailable();
        var profileId = account["id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(profileId)) throw Unavailable();
        var matching = bindings.Where(x => x.ProfileId == profileId).Take(2).ToArray();
        if (matching.Length > 1) throw Unavailable();
        var binding = matching.SingleOrDefault();
        if (binding is null) return null;
        var profile = profiles.SingleOrDefault(x => x.Id == profileId);
        var identity = CompanyBankAccountMatchService.Key(binding.AccountNumber, binding.Bik, binding.Currency);
        if (profileId is null || binding.ProfileAccountKey != CompanyBankAccountBindingIdentity.ProfileKey(profileId)
            || profile is null || profile.Archived || identity is null || profile.Identity != identity)
            throw Unavailable();
        // Invoice/act callers know their ruble-denominated amount/words format.
        // A contract's custom template does not establish its currency by itself.
        if (requiredCurrency is not null && identity.Currency != requiredCurrency)
            throw new SellerSelectionException("Валюта связанного банковского счёта не соответствует рублёвым реквизитам документа. Выберите рублёвый счёт.");
        var source = await db.BankConnectionAccounts.AsNoTracking()
            .Where(x => x.Id == binding.BankConnectionAccountId && x.BankConnection.LegalEntityId == company.Id)
            .Select(x => new { x.BankConnectionId }).SingleOrDefaultAsync(ct);
        if (source is null) throw Unavailable();
        if (db.Database.IsNpgsql())
        {
            var connections = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnections\" WHERE \"Id\" = {source.BankConnectionId} AND \"LegalEntityId\" = {company.Id} FOR SHARE NOWAIT").ToArrayAsync(ct);
            if (connections.Length != 1) throw new SellerSnapshotConflictException();
            var banks = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = {binding.BankConnectionAccountId} AND \"BankConnectionId\" = {source.BankConnectionId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
            if (banks.Length != 1) throw new SellerSnapshotConflictException();
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"MoneyAccounts\" WHERE \"BankConnectionAccountId\" = {binding.BankConnectionAccountId} AND \"CompanyId\" = {company.Id} ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);
        }
        var bank = await db.BankConnectionAccounts.AsNoTracking()
            .Where(x => x.Id == binding.BankConnectionAccountId && x.BankConnectionId == source.BankConnectionId && x.BankConnection.LegalEntityId == company.Id)
            .Select(x => new { x.AccountNumber, x.Bik, x.Currency, x.ExternalAccountId, x.Status, x.IsHidden, x.BankConnection.Provider, ConnectionStatus = x.BankConnection.Status })
            .SingleOrDefaultAsync(ct);
        if (bank is null || bank.ConnectionStatus != BankConnectionStatuses.Active || bank.IsHidden || !BankAccountStatuses.AllowsNewDocuments(bank.Status)
            || bank.Provider != binding.Provider || bank.ExternalAccountId != binding.ExternalAccountId
            || CompanyBankAccountMatchService.Key(bank.AccountNumber, bank.Bik, bank.Currency) != identity)
            throw Unavailable();
        var money = await db.MoneyAccounts.AsNoTracking().Where(x => x.BankConnectionAccountId == binding.BankConnectionAccountId)
            .Select(x => new { x.Id, x.CompanyId, x.Source, x.IsActive, x.BankAccountNumber, x.BankBik, x.Currency }).Take(2).ToArrayAsync(ct);
        if (money.Length > 1 || money.Any(x => x.CompanyId != company.Id || x.Source != MoneyOperationSource.Bank || !x.IsActive
            || CompanyBankAccountMatchService.Key(x.BankAccountNumber, x.BankBik, x.Currency) != identity)) throw Unavailable();
        return new(binding.Id, binding.BankConnectionAccountId, money.SingleOrDefault()?.Id, binding.Provider, binding.ExternalAccountId, identity.Currency);
    }

    private static SellerSelectionException Unavailable()
        => new("Связанный банковский счёт недоступен или его реквизиты изменились. Проверьте счёт компании и банковскую связь перед созданием документа.");
}
