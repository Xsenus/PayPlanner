using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;

namespace PayPlanner.Api.Services.Companies;

public sealed record CompanyBankProfileCandidate(string Id, string Name);
public sealed record CompanyBankAccountMatchGroup(int Id, string ProfileMatchStatus,
    IReadOnlyList<CompanyBankProfileCandidate> ProfileCandidates, IReadOnlyList<int> BankAccountIds,
    IReadOnlyList<int> ManualMoneyAccountCandidateIds, bool HasArchivedProfileCandidates);
public sealed record CompanyBankAccountMatch(int BankAccountId, int? GroupId,
    IReadOnlyList<int> LinkedMoneyAccountIds, IReadOnlyList<string> Issues);
public sealed record CompanyBankAccountMatchReport(int CompanyId, int BankConnectionId, string Version,
    IReadOnlyList<string> ProfileIssues, IReadOnlyList<CompanyBankAccountMatchGroup> Groups, IReadOnlyList<CompanyBankAccountMatch> Accounts);

public sealed class CompanyBankAccountMatchLimitException : Exception
{
    public CompanyBankAccountMatchLimitException() : base("Слишком много счетов для однозначной сверки. Уточните состав счетов компании; неполный список не используется для подтверждения связи.") { }
}

/// <summary>Read-only candidates, never a persisted link or authority to merge financial history.</summary>
public sealed class CompanyBankAccountMatchService(PaymentContext db)
{
    public const int MaximumBankAccounts = 500;
    public const int MaximumMoneyAccounts = 1000;
    public const int MaximumProfileAccounts = 128;

    internal sealed record Identity(string Number, string Bik, string Currency);
    internal sealed record Profile(string? Id, string Name, Identity? Identity, bool Archived);
    private sealed record BankRow(int Id, int ConnectionId, string Provider, string ExternalAccountId, string Number, string? Bik, string Currency, string Status, bool Hidden, string ConnectionStatus);
    private sealed record MoneyRow(int Id, int? CompanyId, int? BankAccountId, MoneyOperationSource Source, string? Number, string? Bik, string Currency, bool Active);

    public async Task<CompanyBankAccountMatchReport?> ReadAsync(int connectionId, CancellationToken ct)
    {
        // The standalone report must not inherit an unrelated writer's snapshot.
        // Pending tracked edits are neither flushed nor cleared: all reads are persisted projections.
        if (db.Database.CurrentTransaction is not null) throw new SellerSnapshotConflictException();
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await ReadCoreAsync(connectionId, ct);
        await snapshot.CommitAsync(ct);
        return report;
    }

    // Only an admitted command may supply its own short transaction. The public
    // read keeps rejecting an inherited transaction and never clears tracked work.
    internal async Task<CompanyBankAccountMatchReport?> ReadCoreAsync(int connectionId, CancellationToken ct, int? excludeBindingId = null)
    {
        if (db.Database.CurrentTransaction is null) throw new SellerSnapshotConflictException();
        var companyId = await db.BankConnections.AsNoTracking().Where(x => x.Id == connectionId)
            .Select(x => (int?)x.LegalEntityId).SingleOrDefaultAsync(ct);
        if (!companyId.HasValue || !(await OwnCompanyRegistry.LoadIdsAsync(db, ct)).Contains(companyId.Value)) return null;
        var company = await db.LegalEntities.AsNoTracking().Where(x => x.Id == companyId && x.ProfileStatus == CompanyProfileStatuses.Active)
            .Select(x => new
            {
                x.Id,
                TooLarge = x.BankAccountsJson != null && x.BankAccountsJson.Length > CompanyBankAccountList.MaxJsonLength,
                Json = x.BankAccountsJson == null || x.BankAccountsJson.Length <= CompanyBankAccountList.MaxJsonLength ? x.BankAccountsJson : null,
            }).SingleOrDefaultAsync(ct);
        if (company is null) return null;
        var profileIssues = new List<string>();
        var profiles = company.TooLarge ? [] : ReadProfiles(company.Json, profileIssues);
        if (company.TooLarge) profileIssues.Add("ProfileAccountsTooLarge");
        var banks = await db.BankConnectionAccounts.AsNoTracking().Where(x => x.BankConnection.LegalEntityId == company.Id)
            .OrderBy(x => x.Id).Select(x => new BankRow(x.Id, x.BankConnectionId, x.BankConnection.Provider, x.ExternalAccountId, x.AccountNumber, x.Bik, x.Currency,
                x.Status, x.IsHidden, x.BankConnection.Status)).Take(MaximumBankAccounts + 1).ToArrayAsync(ct);
        if (banks.Length > MaximumBankAccounts) throw new CompanyBankAccountMatchLimitException();
        var selected = banks.Where(x => x.ConnectionId == connectionId).ToArray();
        var bankIds = banks.Select(x => x.Id).ToArray();
        var money = selected.Length == 0 ? [] : await db.MoneyAccounts.AsNoTracking()
            .Where(x => x.CompanyId == company.Id && x.Source == MoneyOperationSource.Bank || x.BankConnectionAccountId != null && bankIds.Contains(x.BankConnectionAccountId.Value))
            .OrderBy(x => x.Id).Select(x => new MoneyRow(x.Id, x.CompanyId, x.BankConnectionAccountId, x.Source,
                x.BankAccountNumber == null || x.BankAccountNumber.Length <= 128 ? x.BankAccountNumber : null,
                x.BankBik == null || x.BankBik.Length <= 16 ? x.BankBik : null,
                x.Currency.Length <= 8 ? x.Currency : "", x.IsActive)).Take(MaximumMoneyAccounts + 1).ToArrayAsync(ct);
        if (money.Length > MaximumMoneyAccounts) throw new CompanyBankAccountMatchLimitException();
        var bindings = await db.CompanyBankAccountBindings.AsNoTracking()
            .Where(x => x.RevokedAtUtc == null && (x.LegalEntityId == company.Id || bankIds.Contains(x.BankConnectionAccountId))
                && (!excludeBindingId.HasValue || x.Id != excludeBindingId.Value))
            .OrderBy(x => x.Id).Select(x => new
            {
                x.Id,
                x.LegalEntityId,
                x.ProfileAccountKey,
                ProfileId = x.ProfileAccountId.Length <= CompanyBankAccountList.MaxJsonLength ? x.ProfileAccountId : null,
                ProfileIdTooLarge = x.ProfileAccountId.Length > CompanyBankAccountList.MaxJsonLength,
                x.BankConnectionAccountId,
                x.Provider,
                x.ExternalAccountId,
                x.AccountNumber,
                x.Bik,
                x.Currency,
                x.ConfirmedMatchVersion,
                x.CreatedByUserId,
                x.CreatedByUserName,
                x.CreatedAtUtc,
            }).Take(MaximumProfileAccounts + MaximumBankAccounts + 1).ToArrayAsync(ct);
        if (bindings.Length > MaximumProfileAccounts + MaximumBankAccounts) throw new CompanyBankAccountMatchLimitException();
        // Historical confirmations are never deleted/reactivated by commands.
        // Including their last ID prevents an old pre-bind version from becoming
        // valid again after a bind/revoke cycle, without loading the full history.
        var lastBindingId = await db.CompanyBankAccountBindings.AsNoTracking()
            .Where(x => x.LegalEntityId == company.Id && (!excludeBindingId.HasValue || x.Id != excludeBindingId.Value))
            .Select(x => (int?)x.Id).MaxAsync(ct) ?? 0;

        var activeProfiles = profiles.Where(x => !x.Archived && x.Id is not null && x.Identity is not null).ToLookup(x => x.Identity!);
        var archivedProfiles = profiles.Where(x => x.Archived && x.Identity is not null).ToLookup(x => x.Identity!);
        var bankIdentities = banks.Select(x => (Row: x, Identity: Key(x.Number, x.Bik, x.Currency)))
            .Where(x => x.Identity is not null).ToLookup(x => x.Identity!);
        var linkedMoney = money.Where(x => x.BankAccountId.HasValue).ToLookup(x => x.BankAccountId!.Value);
        var manualMoney = money.Where(x => x.BankAccountId is null && x.CompanyId == company.Id && x.Source == MoneyOperationSource.Bank)
            .Select(x => (Row: x, Identity: Key(x.Number, x.Bik, x.Currency))).Where(x => x.Identity is not null).ToLookup(x => x.Identity!);
        // Shared candidates are emitted once per identity, rather than repeated
        // for every duplicate bank row (which would create a quadratic payload).
        var groups = new Dictionary<Identity, CompanyBankAccountMatchGroup>();
        foreach (var bank in selected)
        {
            var key = Key(bank.Number, bank.Bik, bank.Currency);
            if (key is null || groups.ContainsKey(key)) continue;
            var peers = bankIdentities[key].Select(x => x.Row.Id).ToArray();
            var candidates = activeProfiles[key].Select(x => new CompanyBankProfileCandidate(x.Id!, x.Name)).ToArray();
            groups.Add(key, new(peers[0], candidates.Length switch { 0 => "Missing", 1 => "UniqueCandidate", _ => "Ambiguous" },
                candidates, peers, manualMoney[key].Select(x => x.Row.Id).ToArray(), archivedProfiles[key].Any()));
        }
        var rows = new List<CompanyBankAccountMatch>(selected.Length);
        foreach (var bank in selected)
        {
            var identity = Key(bank.Number, bank.Bik, bank.Currency);
            var issues = new List<string>();
            var group = identity is null ? null : groups[identity];
            var linked = linkedMoney[bank.Id].ToArray();
            var validLinked = linked.Where(x => identity is not null && x.CompanyId == company.Id && x.Source == MoneyOperationSource.Bank
                && Key(x.Number, x.Bik, x.Currency) == identity).ToArray();
            if (identity is null) issues.Add("IncompleteBankIdentity");
            if (bank.ConnectionStatus != BankConnectionStatuses.Active) issues.Add("ConnectionUnavailable");
            if (!BankAccountStatuses.AllowsNewDocuments(bank.Status)) issues.Add("BankAccountUnavailable");
            if (bank.Hidden) issues.Add("BankAccountHidden");
            if (group?.HasArchivedProfileCandidates == true) issues.Add("ArchivedProfileCandidates");
            if (group?.BankAccountIds.Count > 1) issues.Add("MultipleBankSources");
            if (linked.Length != validLinked.Length) issues.Add("LinkedMoneyIdentityMismatch");
            if (validLinked.Length > 1) issues.Add("MultipleLinkedMoneyAccounts");
            if (validLinked.Any(x => !x.Active)) issues.Add("LinkedMoneyAccountInactive");
            if (group?.ManualMoneyAccountCandidateIds.Count > 0) issues.Add("ManualMoneyCandidatesRequireReview");
            rows.Add(new(bank.Id, group?.Id, validLinked.Select(x => x.Id).ToArray(), issues));
        }
        // Include all relevant company connections: a duplicate elsewhere must not
        // turn into a false unique candidate. No company images or financial history are loaded.
        var version = "bank-profile-match-v2:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { company.Id, connectionId, company.Json, company.TooLarge, banks, money, bindings, lastBindingId })));
        return new(company.Id, connectionId, version, profileIssues.Distinct(StringComparer.Ordinal).ToArray(), groups.Values.OrderBy(x => x.Id).ToArray(), rows);
    }

    internal static Identity? Key(string? number, string? bik, string? currency)
    {
        var n = BankStatementImportRules.NormalizeBankAccount(number);
        var b = BankStatementImportRules.NormalizeBankAccount(bik);
        var c = MoneyBalanceIdentity.NormalizeCurrency(currency);
        return n.Length == 0 || b.Length == 0 || c is null ? null : new(n, b, c);
    }

    internal static Profile[] ReadProfiles(string? json, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) { issues.Add("InvalidProfileAccounts"); return []; }
            if (parsed.RootElement.GetArrayLength() > MaximumProfileAccounts) throw new CompanyBankAccountMatchLimitException();
            var result = new List<Profile>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { issues.Add("InvalidProfileAccounts"); return []; }
                if (!ValidFlag(item, "isArchived") || !ValidFlag(item, "isPrimary")) { issues.Add("InvalidProfileAccounts"); return []; }
                var id = Text(item, "id");
                if (id is null) issues.Add("MissingStableProfileId");
                else if (!ids.Add(id)) { issues.Add("DuplicateProfileId"); return []; }
                var key = Key(Text(item, "accountNumber"), Text(item, "bik"), Text(item, "currency"));
                if (key is null) issues.Add("IncompleteProfileIdentity");
                var name = Text(item, "name") ?? Text(item, "bank") ?? "";
                result.Add(new(id, name.Length <= 200 ? name : name[..200], key, Flag(item, "isArchived")));
            }
            return result.ToArray();
        }
        catch (JsonException) { issues.Add("InvalidProfileAccounts"); return []; }
    }

    private static string? Text(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    private static bool Flag(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool ValidFlag(JsonElement item, string key)
        => !item.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
}
