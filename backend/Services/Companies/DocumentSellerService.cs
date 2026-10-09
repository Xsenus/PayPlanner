using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Companies;

/// <summary>Документ (счёт, акт), у которого есть выбранный продавец и сохранённый снимок его реквизитов.</summary>
public interface IDocumentSeller
{
    int? SellerCompanyId { get; set; }
    int? SellerBranchId { get; set; }
    string? SellerSnapshotJson { get; set; }
}

/// <summary>
/// Собирает «виртуальное юрлицо» продавца из компании и выбранного подразделения, чтобы существующие
/// генераторы счетов и актов, принимающие <see cref="LegalEntity"/>, не пришлось переписывать,
/// и превращает его в неизменяемый снимок для документа.
/// </summary>
public static class SellerLegalEntityComposer
{
    /// <summary>Текстовые реквизиты, попадающие в документ. Изображения (печать, подпись) в снимок не входят.</summary>
    private sealed record SellerSnapshotData(
        int Id,
        string ShortName,
        string? FullName,
        string? OrganizationForm,
        string? Inn,
        string? Kpp,
        string? Ogrn,
        string? Okpo,
        string? Okved,
        string? TaxSystem,
        bool? VatWithoutVat,
        string? Address,
        string? Phone,
        string? Email,
        string? Director,
        string? DocumentDirectorJson,
        string? ChiefAccountantJson,
        string? BankAccountsJson,
        string? TreasuryBankName,
        string? TreasuryBankBik,
        string? TreasuryAccountNumber,
        string? TreasuryCorrAccountNumber);

    public static LegalEntity Compose(LegalEntity company, CompanyBranch? branch)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (branch is not null && branch.LegalEntityId != company.Id)
        {
            throw new InvalidOperationException("Филиал принадлежит другой компании.");
        }

        var companyAddress = LegalEntityAddressResolver.ResolvePreferredAddress(company);
        var accountsJson = company.BankAccountsJson;
        if (branch is not null
            && CompanyRequisitesResolver.TryFindActiveAccount(company, branch.BankAccountId, out var branchAccount)
            && branchAccount is not null)
        {
            var chosen = (System.Text.Json.Nodes.JsonObject)branchAccount.DeepClone();
            chosen["isPrimary"] = true;
            accountsJson = new System.Text.Json.Nodes.JsonArray(chosen).ToJsonString(
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        return new LegalEntity
        {
            Id = company.Id,
            ShortName = company.ShortName,
            FullName = company.FullName,
            OrganizationForm = company.OrganizationForm,
            Inn = company.Inn,
            Kpp = Pick(branch?.Kpp, company.Kpp),
            Ogrn = company.Ogrn,
            Okpo = company.Okpo,
            Okved = company.Okved,
            TaxSystem = company.TaxSystem,
            VatWithoutVat = company.VatWithoutVat,
            Address = Pick(branch?.Address, companyAddress),
            Phone = company.Phone,
            Email = company.Email,
            Director = company.Director,
            DocumentDirectorJson = Pick(branch?.SignerJson, company.DocumentDirectorJson),
            ChiefAccountantJson = company.ChiefAccountantJson,
            BankAccountsJson = accountsJson,
            TreasuryBankName = company.TreasuryBankName,
            TreasuryBankBik = company.TreasuryBankBik,
            TreasuryAccountNumber = company.TreasuryAccountNumber,
            TreasuryCorrAccountNumber = company.TreasuryCorrAccountNumber,
            LogoDataUrl = company.LogoDataUrl,
            SealDataUrl = company.SealDataUrl,
            DirectorSignatureDataUrl = company.DirectorSignatureDataUrl,
        };
    }

    public static string ToSnapshotJson(LegalEntity seller)
        => JsonSerializer.Serialize(new SellerSnapshotData(
            seller.Id, seller.ShortName, seller.FullName, seller.OrganizationForm, seller.Inn, seller.Kpp,
            seller.Ogrn, seller.Okpo, seller.Okved, seller.TaxSystem, seller.VatWithoutVat,
            seller.Address, seller.Phone, seller.Email, seller.Director, seller.DocumentDirectorJson,
            seller.ChiefAccountantJson, seller.BankAccountsJson, seller.TreasuryBankName,
            seller.TreasuryBankBik, seller.TreasuryAccountNumber, seller.TreasuryCorrAccountNumber),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Восстанавливает продавца из снимка; печать и подпись берутся у <paramref name="liveCompany"/>, если он известен.</summary>
    public static LegalEntity? FromSnapshot(string? json, LegalEntity? liveCompany)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        SellerSnapshotData? data;
        try
        {
            data = JsonSerializer.Deserialize<SellerSnapshotData>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (data is null)
        {
            return null;
        }

        return new LegalEntity
        {
            Id = data.Id,
            ShortName = data.ShortName,
            FullName = data.FullName,
            OrganizationForm = data.OrganizationForm,
            Inn = data.Inn,
            Kpp = data.Kpp,
            Ogrn = data.Ogrn,
            Okpo = data.Okpo,
            Okved = data.Okved,
            TaxSystem = data.TaxSystem,
            VatWithoutVat = data.VatWithoutVat,
            Address = data.Address,
            Phone = data.Phone,
            Email = data.Email,
            Director = data.Director,
            DocumentDirectorJson = data.DocumentDirectorJson,
            ChiefAccountantJson = data.ChiefAccountantJson,
            BankAccountsJson = data.BankAccountsJson,
            TreasuryBankName = data.TreasuryBankName,
            TreasuryBankBik = data.TreasuryBankBik,
            TreasuryAccountNumber = data.TreasuryAccountNumber,
            TreasuryCorrAccountNumber = data.TreasuryCorrAccountNumber,
            LogoDataUrl = liveCompany?.LogoDataUrl,
            SealDataUrl = liveCompany?.SealDataUrl,
            DirectorSignatureDataUrl = liveCompany?.DirectorSignatureDataUrl,
        };
    }

    private static string? Pick(string? branchValue, string? companyValue)
        => string.IsNullOrWhiteSpace(branchValue) ? companyValue : branchValue.Trim();
}

public interface IDocumentSellerService
{
    /// <summary>Проверяет выбор продавца: компания собственная и активная, подразделение её и активное. <c>null</c> — всё в порядке.</summary>
    Task<string?> ValidateSelectionAsync(int? companyId, int? branchId, CancellationToken ct);

    /// <summary>
    /// Возвращает продавца для печатной формы. Если у документа есть снимок — по нему; иначе по выбору документа
    /// (или компании по умолчанию). При <paramref name="freeze"/> снимок сохраняется в документ; вызывающий сохраняет изменения.
    /// </summary>
    Task<LegalEntity?> ResolveAsync(IDocumentSeller document, LegalEntity? defaultCompany, bool freeze, CancellationToken ct);
}

public sealed class DocumentSellerService : IDocumentSellerService
{
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";

    private readonly PaymentContext _db;

    public DocumentSellerService(PaymentContext db)
    {
        _db = db;
    }

    public async Task<string?> ValidateSelectionAsync(int? companyId, int? branchId, CancellationToken ct)
    {
        if (branchId.HasValue && !companyId.HasValue)
        {
            return "Для выбора подразделения укажите компанию.";
        }

        if (!companyId.HasValue)
        {
            return null;
        }

        var ownIds = await LoadOwnCompanyIdsAsync(ct);
        if (!ownIds.Contains(companyId.Value))
        {
            return "Продавцом можно выбрать только собственную компанию.";
        }

        var status = await _db.LegalEntities
            .AsNoTracking()
            .Where(le => le.Id == companyId.Value)
            .Select(le => le.ProfileStatus)
            .FirstOrDefaultAsync(ct);
        if (status != CompanyProfileStatuses.Active)
        {
            return "Компания-продавец должна быть активной.";
        }

        if (branchId.HasValue)
        {
            var branch = await _db.CompanyBranches
                .AsNoTracking()
                .Where(b => b.Id == branchId.Value)
                .Select(b => new { b.LegalEntityId, b.Status })
                .FirstOrDefaultAsync(ct);
            if (branch is null || branch.LegalEntityId != companyId.Value)
            {
                return "Подразделение не принадлежит выбранной компании.";
            }

            if (branch.Status != CompanyProfileStatuses.Active)
            {
                return "Подразделение в архиве.";
            }
        }

        return null;
    }

    public async Task<LegalEntity?> ResolveAsync(
        IDocumentSeller document,
        LegalEntity? defaultCompany,
        bool freeze,
        CancellationToken ct)
    {
        var companyId = document.SellerCompanyId ?? defaultCompany?.Id;

        if (!string.IsNullOrWhiteSpace(document.SellerSnapshotJson))
        {
            var live = companyId.HasValue ? await LoadCompanyAsync(companyId.Value, defaultCompany, ct) : null;
            var fromSnapshot = SellerLegalEntityComposer.FromSnapshot(document.SellerSnapshotJson, live);
            if (fromSnapshot is not null)
            {
                return fromSnapshot;
            }
        }

        if (!companyId.HasValue)
        {
            return defaultCompany;
        }

        var company = await LoadCompanyAsync(companyId.Value, defaultCompany, ct);
        if (company is null)
        {
            return defaultCompany;
        }

        CompanyBranch? branch = null;
        if (document.SellerBranchId.HasValue)
        {
            branch = await _db.CompanyBranches
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == document.SellerBranchId.Value && b.LegalEntityId == company.Id, ct);
        }

        var composed = SellerLegalEntityComposer.Compose(company, branch);
        if (freeze)
        {
            document.SellerCompanyId ??= company.Id;
            document.SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(composed);
        }

        return composed;
    }

    private async Task<LegalEntity?> LoadCompanyAsync(int id, LegalEntity? defaultCompany, CancellationToken ct)
        => defaultCompany is not null && defaultCompany.Id == id
            ? defaultCompany
            : await _db.LegalEntities.AsNoTracking().Include(le => le.Addresses).FirstOrDefaultAsync(le => le.Id == id, ct);

    private async Task<HashSet<int>> LoadOwnCompanyIdsAsync(CancellationToken ct)
    {
        var values = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == MainCompanyIdsSettingKey || item.Key == LegacyMainCompanySettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var ids = new HashSet<int>();
        if (values.TryGetValue(MainCompanyIdsSettingKey, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                foreach (var id in JsonSerializer.Deserialize<List<int>>(raw) ?? [])
                {
                    if (id > 0)
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (JsonException)
            {
                // Некорректное значение настройки игнорируется, как и в остальных местах.
            }
        }

        if (ids.Count == 0
            && values.TryGetValue(LegacyMainCompanySettingKey, out var legacy)
            && int.TryParse(legacy, out var legacyId)
            && legacyId > 0)
        {
            ids.Add(legacyId);
        }

        return ids;
    }
}
