using System.Text.Json.Nodes;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Снимок реквизитов продавца на момент создания документа. После сохранения вместе с документом
/// он не меняется при правке профиля компании, поэтому прежние документы остаются прежними.
/// </summary>
public sealed record CompanyRequisitesSnapshot(
    int LegalEntityId,
    int? BranchId,
    string? BranchName,
    string? BranchKind,
    string Name,
    string? FullName,
    string? Inn,
    string? Kpp,
    string? Ogrn,
    string Address,
    string? BankAccountId,
    string? BankName,
    string? Bik,
    string? AccountNumber,
    string? CorrAccountNumber,
    string? SignerJson,
    DateTime CapturedAtUtc,
    IReadOnlyDictionary<string, string> Sources);

/// <summary>
/// Собирает реквизиты для документа из профиля компании и, при выборе, филиала:
/// заполненное поле филиала переопределяет значение компании, остальное наследуется.
/// Ни ИНН, ни ОГРН, ни название у филиала не переопределяются.
/// </summary>
public static class CompanyRequisitesResolver
{
    public const string CompanySource = "company";
    public const string BranchSource = "branch";

    /// <param name="company">Компания с загруженными адресами (<see cref="LegalEntity.Addresses"/>).</param>
    /// <param name="branch">Выбранный филиал этой же компании либо <c>null</c>.</param>
    public static CompanyRequisitesSnapshot Resolve(LegalEntity company, CompanyBranch? branch, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (branch is not null && branch.LegalEntityId != company.Id)
        {
            throw new InvalidOperationException("Филиал принадлежит другой компании.");
        }

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        var kpp = Pick("kpp", branch?.Kpp, company.Kpp, sources);

        var companyAddress = LegalEntityAddressResolver.ResolvePreferredAddress(company);
        var address = Pick("address", branch?.Address, companyAddress, sources) ?? string.Empty;

        var account = ResolveAccount(company, branch, sources);

        var signer = Pick("signer", branch?.SignerJson, company.DocumentDirectorJson, sources);

        return new CompanyRequisitesSnapshot(
            company.Id,
            branch?.Id,
            branch?.Name,
            branch?.Kind,
            company.ShortName,
            company.FullName,
            company.Inn?.Trim(),
            kpp,
            company.Ogrn?.Trim(),
            address,
            ReadString(account, "id"),
            ReadString(account, "bank") ?? Clean(company.TreasuryBankName),
            ReadString(account, "bik") ?? Clean(company.TreasuryBankBik),
            ReadString(account, "accountNumber") ?? Clean(company.TreasuryAccountNumber),
            ReadString(account, "corrAccountNumber") ?? Clean(company.TreasuryCorrAccountNumber),
            signer,
            nowUtc,
            sources);
    }

    /// <summary>
    /// Находит счёт, привязанный к филиалу. Возвращает <c>false</c>, если такого активного счёта
    /// нет в списке компании — в том числе когда идентификатор принадлежит счёту другой компании.
    /// </summary>
    public static bool TryFindActiveAccount(LegalEntity company, string? accountId, out JsonObject? account)
    {
        account = null;
        var id = Clean(accountId);
        if (id is null)
        {
            return false;
        }

        var normalized = CompanyBankAccountList.Normalize(company.BankAccountsJson, static () => string.Empty);
        if (normalized.Json is null || JsonNode.Parse(normalized.Json) is not JsonArray array)
        {
            return false;
        }

        account = array.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(ReadString(item, "id"), id, StringComparison.Ordinal)
            && !(item["isArchived"]?.GetValue<bool>() ?? false));
        return account is not null;
    }

    private static JsonObject? ResolveAccount(LegalEntity company, CompanyBranch? branch, Dictionary<string, string> sources)
    {
        if (branch is not null && TryFindActiveAccount(company, branch.BankAccountId, out var branchAccount))
        {
            sources["bankAccount"] = BranchSource;
            return branchAccount;
        }

        sources["bankAccount"] = CompanySource;
        return CompanyBankAccountList.FindPrimaryActive(company.BankAccountsJson);
    }

    private static string? Pick(string field, string? branchValue, string? companyValue, Dictionary<string, string> sources)
    {
        var overridden = Clean(branchValue);
        sources[field] = overridden is null ? CompanySource : BranchSource;
        return overridden ?? Clean(companyValue);
    }

    private static string? ReadString(JsonObject? item, string name)
        => item is not null && item.TryGetPropertyValue(name, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var text)
            ? Clean(text)
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
