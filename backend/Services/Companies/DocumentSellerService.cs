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
        string? BankAccountsJson,
        string? TreasuryBankName,
        string? TreasuryBankBik,
        string? TreasuryAccountNumber,
        string? TreasuryCorrAccountNumber,
        int? LogoVersionId = null,
        int? SealVersionId = null,
        int? SignatureVersionId = null,
        bool? ImagesFrozen = null);

    public static LegalEntity Compose(LegalEntity company, CompanyBranch? branch)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (branch is not null && branch.LegalEntityId != company.Id)
        {
            throw new InvalidOperationException("Филиал принадлежит другой компании.");
        }

        var companyAddress = LegalEntityAddressResolver.ResolvePreferredAddress(company);
        // В виртуальное юрлицо попадает ровно один счёт — тот, что подставит документ: выбранный у подразделения
        // либо основной активный. Так снимок не раздувается списком всех счетов компании.
        System.Text.Json.Nodes.JsonObject? chosenAccount = null;
        if (branch is not null
            && CompanyRequisitesResolver.TryFindActiveAccount(company, branch.BankAccountId, out var branchAccount))
        {
            chosenAccount = branchAccount;
        }

        chosenAccount ??= CompanyBankAccountList.FindPrimaryActive(company.BankAccountsJson);
        string? accountsJson = null;
        if (chosenAccount is not null)
        {
            var clone = (System.Text.Json.Nodes.JsonObject)chosenAccount.DeepClone();
            clone["isPrimary"] = true;
            accountsJson = new System.Text.Json.Nodes.JsonArray(clone).ToJsonString(
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

    public static string ToSnapshotJson(LegalEntity seller, int? logoVersionId = null, int? sealVersionId = null, int? signatureVersionId = null, bool imagesFrozen = false)
        => JsonSerializer.Serialize(new SellerSnapshotData(
            seller.Id, seller.ShortName, seller.FullName, seller.OrganizationForm, seller.Inn, seller.Kpp,
            seller.Ogrn, seller.Okpo, seller.Okved, seller.TaxSystem, seller.VatWithoutVat,
            seller.Address, seller.Phone, seller.Email, seller.Director, seller.DocumentDirectorJson,
            seller.BankAccountsJson, seller.TreasuryBankName,
            seller.TreasuryBankBik, seller.TreasuryAccountNumber, seller.TreasuryCorrAccountNumber,
            logoVersionId, sealVersionId, signatureVersionId, imagesFrozen ? true : null),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Номера версий фирменных изображений из снимка (пусто у старых снимков, где изображения берутся из профиля).</summary>
    public static IReadOnlyList<int> ReadImageVersionIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var data = JsonSerializer.Deserialize<SellerSnapshotData>(json);
            return data is null ? [] : new int?[] { data.LogoVersionId, data.SealVersionId, data.SignatureVersionId }.Where(id => id.HasValue).Select(id => id!.Value).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Восстанавливает продавца из снимка. Изображения берутся из неизменяемых версий (<paramref name="versionImages"/>),
    /// а у старых снимков без версий — из профиля <paramref name="liveCompany"/>, если он известен.
    /// </summary>
    public static LegalEntity? FromSnapshot(string? json, LegalEntity? liveCompany, IReadOnlyDictionary<int, string>? versionImages = null)
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
            BankAccountsJson = data.BankAccountsJson,
            TreasuryBankName = data.TreasuryBankName,
            TreasuryBankBik = data.TreasuryBankBik,
            TreasuryAccountNumber = data.TreasuryAccountNumber,
            TreasuryCorrAccountNumber = data.TreasuryCorrAccountNumber,
            LogoDataUrl = ImageOf(data.ImagesFrozen, data.LogoVersionId, liveCompany?.LogoDataUrl, versionImages),
            SealDataUrl = ImageOf(data.ImagesFrozen, data.SealVersionId, liveCompany?.SealDataUrl, versionImages),
            DirectorSignatureDataUrl = ImageOf(data.ImagesFrozen, data.SignatureVersionId, liveCompany?.DirectorSignatureDataUrl, versionImages),
        };
    }

    // Снимок с зафиксированными изображениями: берём только сохранённую версию (или «изображения не было»), даже если
    // профиль уже изменён. У старого снимка без пометки изображения по-прежнему берутся из профиля.
    private static string? ImageOf(bool? imagesFrozen, int? versionId, string? live, IReadOnlyDictionary<int, string>? versionImages)
        => imagesFrozen == true
            ? (versionId.HasValue && versionImages is not null && versionImages.TryGetValue(versionId.Value, out var stored) ? stored : null)
            : live;

    private static string? Pick(string? branchValue, string? companyValue)
        => string.IsNullOrWhiteSpace(branchValue) ? companyValue : branchValue.Trim();
}

/// <summary>Реквизиты продавца не помещаются в снимок документа: выдачу нужно остановить, а не оставлять документ без фиксации.</summary>
public sealed class SellerSnapshotTooLargeException(int length, int limit) : Exception($"Реквизиты продавца ({length} символов) не помещаются в снимок документа (предел {limit}). Сократите адрес, данные подписанта или лишние поля в профиле компании.")
{
    public int Length { get; } = length;
    public int Limit { get; } = limit;
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
    public const int MaxSnapshotLength = 16000;

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
            var versionIds = SellerLegalEntityComposer.ReadImageVersionIds(document.SellerSnapshotJson);
            var versionImages = versionIds.Count == 0
                ? null
                : await _db.CompanyBrandImageVersions.AsNoTracking()
                    .Where(version => versionIds.Contains(version.Id))
                    .ToDictionaryAsync(version => version.Id, version => version.DataUrl, ct);
            var fromSnapshot = SellerLegalEntityComposer.FromSnapshot(document.SellerSnapshotJson, live, versionImages);
            if (fromSnapshot is not null)
            {
                return fromSnapshot;
            }
        }

        if (!companyId.HasValue)
        {
            return defaultCompany;
        }

        // Выбор мог устареть после сохранения: компанию убрали из собственных, архивировали, подразделение закрыли.
        // Такой выбор не применяется и не фиксируется в документе — берётся компания по умолчанию.
        var selectionIsUsable = !document.SellerCompanyId.HasValue
            || await ValidateSelectionAsync(document.SellerCompanyId, document.SellerBranchId, ct) is null;
        if (!selectionIsUsable)
        {
            if (defaultCompany is null)
            {
                return null;
            }

            companyId = defaultCompany.Id;
        }

        var company = await LoadCompanyAsync(companyId.Value, defaultCompany, ct);
        if (company is null)
        {
            return defaultCompany;
        }

        CompanyBranch? branch = null;
        if (selectionIsUsable && document.SellerBranchId.HasValue)
        {
            branch = await _db.CompanyBranches
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == document.SellerBranchId.Value && b.LegalEntityId == company.Id, ct);
        }

        var composed = SellerLegalEntityComposer.Compose(company, branch);
        if (freeze)
        {
            // Текст ограничен колонкой, а изображения лежат в неизменяемых версиях и в JSON попадают только номерами.
            var logo = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Logo, composed.LogoDataUrl, ct);
            var seal = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Seal, composed.SealDataUrl, ct);
            var signature = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Signature, composed.DirectorSignatureDataUrl, ct);
            var snapshot = SellerLegalEntityComposer.ToSnapshotJson(composed, logo, seal, signature, imagesFrozen: true);
            if (snapshot.Length > MaxSnapshotLength)
            {
                // Молча оставить документ «живым» нельзя: выданный файл потеряет защиту от правок профиля.
                throw new SellerSnapshotTooLargeException(snapshot.Length, MaxSnapshotLength);
            }

            document.SellerCompanyId = company.Id;
            document.SellerBranchId = branch?.Id;
            document.SellerSnapshotJson = snapshot;
        }

        return composed;
    }

    /// <summary>
    /// Возвращает номер неизменяемой версии изображения (создаёт её при первой фиксации такого содержимого).
    /// Параллельная фиксация того же изображения обрабатывается уникальным индексом: проигравший читает версию победителя.
    /// </summary>
    private async Task<int?> EnsureImageVersionAsync(int companyId, string kind, string? dataUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataUrl)));
        var existing = await _db.CompanyBrandImageVersions.AsNoTracking()
            .Where(item => item.LegalEntityId == companyId && item.Kind == kind && item.Sha256 == hash)
            .Select(item => (int?)item.Id)
            .FirstOrDefaultAsync(ct);
        if (existing.HasValue)
        {
            return existing;
        }

        var version = new CompanyBrandImageVersion { LegalEntityId = companyId, Kind = kind, Sha256 = hash, DataUrl = dataUrl };
        _db.CompanyBrandImageVersions.Add(version);
        try
        {
            await _db.SaveChangesAsync(ct);
            return version.Id;
        }
        catch (DbUpdateException)
        {
            _db.Entry(version).State = EntityState.Detached;
            return await _db.CompanyBrandImageVersions.AsNoTracking()
                .Where(item => item.LegalEntityId == companyId && item.Kind == kind && item.Sha256 == hash)
                .Select(item => (int?)item.Id)
                .FirstAsync(ct);
        }
    }

    private async Task<LegalEntity?> LoadCompanyAsync(int id, LegalEntity? defaultCompany, CancellationToken ct)
        => defaultCompany is not null && defaultCompany.Id == id
            ? defaultCompany
            : await _db.LegalEntities.AsNoTracking().Include(le => le.Addresses).FirstOrDefaultAsync(le => le.Id == id, ct);

    private Task<HashSet<int>> LoadOwnCompanyIdsAsync(CancellationToken ct)
        => OwnCompanyRegistry.LoadIdsAsync(_db, ct);
}
