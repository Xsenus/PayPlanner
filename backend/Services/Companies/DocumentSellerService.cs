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
    string? SellerSnapshotPolicy { get; set; }
}

public sealed record SellerSnapshotMetadata(DateTime? FrozenAtUtc, string? FreezeReason, bool LegacyImages,
    string? CompanyName = null, string? BranchName = null);

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
        bool? ImagesFrozen = null,
        DateTime? FrozenAtUtc = null,
        string? FreezeReason = null,
        string? BranchName = null);

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

    public static string ToSnapshotJson(LegalEntity seller, int? logoVersionId = null, int? sealVersionId = null, int? signatureVersionId = null, bool imagesFrozen = false,
        DateTime? frozenAtUtc = null, string? freezeReason = null, string? branchName = null)
        => JsonSerializer.Serialize(new SellerSnapshotData(
            seller.Id, seller.ShortName, seller.FullName, seller.OrganizationForm, seller.Inn, seller.Kpp,
            seller.Ogrn, seller.Okpo, seller.Okved, seller.TaxSystem, seller.VatWithoutVat,
            seller.Address, seller.Phone, seller.Email, seller.Director, seller.DocumentDirectorJson,
            seller.BankAccountsJson, seller.TreasuryBankName,
            seller.TreasuryBankBik, seller.TreasuryAccountNumber, seller.TreasuryCorrAccountNumber,
            logoVersionId, sealVersionId, signatureVersionId, imagesFrozen ? true : null, frozenAtUtc, freezeReason, branchName),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static SellerSnapshotMetadata? ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<SellerSnapshotData>(json);
            return data is null ? null : new SellerSnapshotMetadata(data.FrozenAtUtc, data.FreezeReason, data.ImagesFrozen != true,
                data.ShortName, data.BranchName);
        }
        catch (JsonException) { return null; }
    }

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
    Task<string> ReadCreationPolicyAsync(CancellationToken ct);

    /// <summary>Вызывается только для нового документа в его атомарной транзакции, после проверки seller choice.</summary>
    Task InitializeForCreationAsync(IDocumentSeller document, LegalEntity? defaultCompany, CancellationToken ct);

    /// <summary>Проверяет выбор продавца: компания собственная и активная, подразделение её и активное. <c>null</c> — всё в порядке.</summary>
    Task<string?> ValidateSelectionAsync(int? companyId, int? branchId, CancellationToken ct);

    /// <summary>
    /// Возвращает продавца для печатной формы. Если у документа есть снимок — по нему; иначе по выбору документа
    /// (или компании по умолчанию). При <paramref name="freeze"/> снимок сохраняется в документ; вызывающий сохраняет изменения.
    /// </summary>
    Task<LegalEntity?> ResolveAsync(IDocumentSeller document, LegalEntity? defaultCompany, bool freeze, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? canStillRead = null);
    Task UpdateSelectionAsync(IDocumentSeller document, int? companyId, int? branchId, CancellationToken ct,
        Func<CancellationToken, Task<bool>> canStillEdit);
}

public sealed class DocumentSellerService : IDocumentSellerService
{
    public const int MaxSnapshotLength = 16000;

    private readonly PaymentContext _db;

    public DocumentSellerService(PaymentContext db)
    {
        _db = db;
    }

    public async Task<string> ReadCreationPolicyAsync(CancellationToken ct)
    {
        var value = await _db.SystemSettings.AsNoTracking()
            .Where(item => item.Key == DocumentSellerSnapshotPolicy.SettingKey)
            .Select(item => item.Value).FirstOrDefaultAsync(ct);
        return DocumentSellerSnapshotPolicy.Effective(value);
    }

    public async Task InitializeForCreationAsync(IDocumentSeller document, LegalEntity? defaultCompany, CancellationToken ct)
    {
        // Существующий snapshot имеет приоритет; его JSON/дата/версии не обновляются настройкой.
        if (!string.IsNullOrWhiteSpace(document.SellerSnapshotJson)) return;
        var policy = document.SellerSnapshotPolicy ?? await ReadCreationPolicyAsync(ct);
        document.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.Effective(policy);
        if (document.SellerSnapshotPolicy != DocumentSellerSnapshotPolicy.OnCreate)
        {
            if (await ValidateSelectionAsync(document.SellerCompanyId, document.SellerBranchId, ct) is { } error)
                throw new SellerSelectionException(error);
            return;
        }
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Создание документа со снимком требует общей транзакции.");
        // New private records may have a reserved server ID before being inserted.
        // Their creator owns the transaction; do not mistake that ID for a persisted root.
        var seller = await DocumentSellerLease.RunAsync(_db,
            lease => ResolveCoreAsync(document, defaultCompany, freeze: true, lease, ct, strictSelection: true), ct);
        if (seller is null || string.IsNullOrWhiteSpace(document.SellerSnapshotJson))
            throw new SellerSelectionException("Выберите доступную собственную компанию до создания документа.");
    }

    public async Task<string?> ValidateSelectionAsync(int? companyId, int? branchId, CancellationToken ct)
        => await ValidateSelectionCoreAsync(companyId, branchId, await LoadOwnCompanyIdsAsync(ct), ct);

    private async Task<string?> ValidateSelectionCoreAsync(int? companyId, int? branchId, HashSet<int> ownIds, CancellationToken ct)
    {
        if (branchId.HasValue && !companyId.HasValue)
        {
            return "Для выбора подразделения укажите компанию.";
        }

        if (!companyId.HasValue)
        {
            return null;
        }

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
        CancellationToken ct,
        Func<CancellationToken, Task<bool>>? canStillRead = null)
    {
        if (!freeze) return await ResolveCoreAsync(document, defaultCompany, false, null, ct);
        var id = DocumentId(document);
        if (id == 0)
            return await DocumentSellerLease.RunAsync(_db,
                lease => ResolveCoreAsync(document, defaultCompany, true, lease, ct), ct);

        DocumentSellerLease.RequireCleanTracker(_db);
        return await DocumentSellerLease.RunAsync(_db, async lease =>
        {
            var rows = await lease.LockDocumentAsync(document, ct);
            if (canStillRead is not null && !await canStillRead(ct)) throw new SellerSnapshotConflictException();
            lease.AdmitCleanTracker();
            var current = new Act();
            CopySnapshot(rows.Single(row => DocumentId(row) == id), current);
            var winners = rows.Where(row => !string.IsNullOrWhiteSpace(row.SellerSnapshotJson)).ToArray();
            var winner = winners.FirstOrDefault();
            if (winner is not null)
            {
                if (winners.Any(row => row.SellerSnapshotJson != winner.SellerSnapshotJson
                    || row.SellerCompanyId != winner.SellerCompanyId || row.SellerBranchId != winner.SellerBranchId))
                    throw new SellerSnapshotConflictException();
                CopySnapshot(winner, current);
            }
            var seller = await ResolveCoreAsync(current, defaultCompany, true, lease, ct);
            if (!string.IsNullOrWhiteSpace(current.SellerSnapshotJson))
            {
                DocumentSellerLease.RequireCleanTracker(_db);
                var tracked = TrackedSellers();
                foreach (var row in rows)
                {
                    // Expense invoices keep their existing counterparty print semantics.
                    if (row is Payment { Type: PaymentType.Expense }) continue;
                    PersistSnapshotFields(row, current, tracked);
                }
                await _db.SaveChangesAsync(ct);
            }
            CopySnapshot(current, document);
            return seller;
        }, ct);
    }

    public async Task UpdateSelectionAsync(IDocumentSeller document, int? companyId, int? branchId, CancellationToken ct,
        Func<CancellationToken, Task<bool>> canStillEdit)
    {
        DocumentSellerLease.RequireCleanTracker(_db);
        await DocumentSellerLease.RunAsync(_db, async lease =>
        {
            var rows = await lease.LockDocumentAsync(document, ct);
            if (!await canStillEdit(ct)) throw new SellerSnapshotConflictException();
            if (rows.Any(row => !string.IsNullOrWhiteSpace(row.SellerSnapshotJson))) throw new SellerSnapshotConflictException();
            lease.AdmitCleanTracker();
            var registry = await OwnCompanyRegistry.ReadStateAsync(_db, ct);
            if (await ValidateSelectionCoreAsync(companyId, branchId, registry.OwnIds(), ct) is { } error)
                throw new SellerSelectionException(error);
            if (companyId.HasValue)
            {
                await lease.LockRegistryAsync(ct);
                if (registry != await OwnCompanyRegistry.ReadStateAsync(_db, ct)) throw new SellerSnapshotConflictException();
                await lease.LockCompanyAsync(companyId.Value, branchId, ct);
                if (registry != await OwnCompanyRegistry.ReadStateAsync(_db, ct)
                    || await ValidateSelectionCoreAsync(companyId, branchId, registry.OwnIds(), ct) is not null)
                    throw new SellerSnapshotConflictException();
            }
            DocumentSellerLease.RequireCleanTracker(_db);
            var tracked = TrackedSellers();
            foreach (var row in rows)
            {
                var selection = new Act
                {
                    SellerCompanyId = companyId,
                    SellerBranchId = companyId.HasValue ? branchId : null,
                    SellerSnapshotPolicy = row.SellerSnapshotPolicy
                };
                PersistSnapshotFields(row, selection, tracked);
            }
            await _db.SaveChangesAsync(ct);
            document.SellerCompanyId = companyId;
            document.SellerBranchId = companyId.HasValue ? branchId : null;
            return true;
        }, ct);
    }

    private static int DocumentId(IDocumentSeller document) => document switch
    {
        Payment payment => payment.Id,
        Act act => act.Id,
        Contract contract => contract.Id,
        _ => throw new NotSupportedException("Неизвестный документ продавца."),
    };

    private static void CopySnapshot(IDocumentSeller source, IDocumentSeller target)
    {
        target.SellerCompanyId = source.SellerCompanyId;
        target.SellerBranchId = source.SellerBranchId;
        target.SellerSnapshotJson = source.SellerSnapshotJson;
        target.SellerSnapshotPolicy = source.SellerSnapshotPolicy;
    }

    private Dictionary<(Type Type, int Id), object> TrackedSellers()
        => _db.ChangeTracker.Entries().Where(entry => entry.Entity is IDocumentSeller)
            .ToDictionary(entry => (entry.Entity.GetType(), DocumentId((IDocumentSeller)entry.Entity)), entry => entry.Entity);

    private void PersistSnapshotFields(IDocumentSeller persisted, IDocumentSeller value, Dictionary<(Type Type, int Id), object> tracked)
    {
        var key = (persisted.GetType(), DocumentId(persisted));
        if (!tracked.TryGetValue(key, out var entity)) tracked.Add(key, entity = persisted);
        var entry = _db.Entry(entity);
        if (entry.State == EntityState.Detached) entry.State = EntityState.Unchanged;
        Set(nameof(IDocumentSeller.SellerCompanyId), persisted.SellerCompanyId, value.SellerCompanyId);
        Set(nameof(IDocumentSeller.SellerBranchId), persisted.SellerBranchId, value.SellerBranchId);
        Set(nameof(IDocumentSeller.SellerSnapshotJson), persisted.SellerSnapshotJson, value.SellerSnapshotJson);
        Set(nameof(IDocumentSeller.SellerSnapshotPolicy), persisted.SellerSnapshotPolicy, value.SellerSnapshotPolicy);

        void Set(string name, object? before, object? after)
        {
            var property = entry.Property(name);
            property.OriginalValue = before;
            property.CurrentValue = after;
            property.IsModified = !Equals(before, after);
        }
    }

    private async Task<LegalEntity?> ResolveCoreAsync(IDocumentSeller document, LegalEntity? defaultCompany, bool freeze,
        DocumentSellerLease? lease, CancellationToken ct, bool strictSelection = false)
    {
        var companyId = document.SellerCompanyId ?? defaultCompany?.Id;

        if (!string.IsNullOrWhiteSpace(document.SellerSnapshotJson))
        {
            var metadata = SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson);
            var live = metadata?.LegacyImages == true && companyId.HasValue ? await LoadCompanyAsync(companyId.Value, defaultCompany, ct) : null;
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
            throw new SellerSelectionException("Сохранённые реквизиты документа повреждены. Автоматическая замена текущим профилем недоступна.");
        }

        LegalEntity? company;
        CompanyBranch? branch = null;
        if (freeze && lease is not null)
        {
            var registry = await OwnCompanyRegistry.ReadStateAsync(_db, ct);
            var selected = await SelectForFreezeAsync(document, registry, strictSelection, ct);
            if (!selected.CompanyId.HasValue) return null;
            // Never lease an arbitrary unavailable ID. Determine the one admitted
            // target from metadata first, then require that same decision after locks.
            await lease.LockRegistryAsync(ct);
            if (registry != await OwnCompanyRegistry.ReadStateAsync(_db, ct)) throw new SellerSnapshotConflictException();
            await lease.LockCompanyAsync(selected.CompanyId.Value, selected.BranchId, ct);
            var freshRegistry = await OwnCompanyRegistry.ReadStateAsync(_db, ct);
            if (registry != freshRegistry)
                throw new SellerSnapshotConflictException();
            FreezeSelection freshSelection;
            try { freshSelection = await SelectForFreezeAsync(document, freshRegistry, false, ct); }
            catch (SellerSelectionException) { throw new SellerSnapshotConflictException(); }
            if (selected != freshSelection) throw new SellerSnapshotConflictException();
            company = await _db.LegalEntities.AsNoTracking().Include(item => item.Addresses)
                .SingleOrDefaultAsync(item => item.Id == selected.CompanyId, ct);
            if (company is null) throw new SellerSnapshotConflictException();
            if (selected.BranchId.HasValue)
                branch = await _db.CompanyBranches.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == selected.BranchId && item.LegalEntityId == company.Id, ct)
                    ?? throw new SellerSnapshotConflictException();
        }
        else
        {
            if (!companyId.HasValue) return defaultCompany;
            // Historical preview fallback is unchanged and never writes a snapshot.
            var usable = !document.SellerCompanyId.HasValue || await ValidateSelectionAsync(document.SellerCompanyId, document.SellerBranchId, ct) is null;
            if (!usable) companyId = defaultCompany?.Id;
            if (!companyId.HasValue) return null;
            company = await LoadCompanyAsync(companyId.Value, defaultCompany, ct);
            if (company is null) return defaultCompany;
            if (usable && document.SellerBranchId.HasValue)
                branch = await _db.CompanyBranches.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == document.SellerBranchId && item.LegalEntityId == company.Id, ct);
        }

        var composed = SellerLegalEntityComposer.Compose(company, branch);
        if (freeze)
        {
            if (!_db.Database.IsSqlite() && !_db.Database.IsNpgsql())
                throw new NotSupportedException("Снимки продавца поддерживаются только SQLite и PostgreSQL.");
            // Не SaveChanges: чужие Added/Modified/Deleted entries остаются только в памяти вызывающего контекста.
            // Если транзакция уже есть, версии и документ commit/rollback вместе с ней.
            await using var versionTransaction = _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(ct) : null;
            var logo = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Logo, composed.LogoDataUrl, ct);
            var seal = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Seal, composed.SealDataUrl, ct);
            var signature = await EnsureImageVersionAsync(company.Id, CompanyBrandImageKinds.Signature, composed.DirectorSignatureDataUrl, ct);
            var snapshot = SellerLegalEntityComposer.ToSnapshotJson(composed, logo, seal, signature, imagesFrozen: true,
                frozenAtUtc: DateTime.UtcNow, freezeReason: DocumentSellerSnapshotPolicy.Effective(document.SellerSnapshotPolicy), branchName: branch?.Name);
            if (snapshot.Length > MaxSnapshotLength)
            {
                // Молча оставить документ «живым» нельзя: выданный файл потеряет защиту от правок профиля.
                throw new SellerSnapshotTooLargeException(snapshot.Length, MaxSnapshotLength);
            }

            if (versionTransaction is not null) await versionTransaction.CommitAsync(ct);

            document.SellerCompanyId = company.Id;
            document.SellerBranchId = branch?.Id;
            document.SellerSnapshotJson = snapshot;
        }

        return composed;
    }

    private sealed record FreezeSelection(int? CompanyId, int? BranchId, string? OriginalSelectionError);

    private async Task<FreezeSelection> SelectForFreezeAsync(IDocumentSeller document, OwnCompanyRegistry.State registry,
        bool strictSelection, CancellationToken ct)
    {
        var ownIds = registry.OwnIds();
        var error = await ValidateSelectionCoreAsync(document.SellerCompanyId, document.SellerBranchId, ownIds, ct);
        if (strictSelection && error is not null) throw new SellerSelectionException(error);
        if (error is null && document.SellerCompanyId.HasValue)
            return new(document.SellerCompanyId, document.SellerBranchId, null);
        var defaultId = registry.SelectedDefaultId();
        if (defaultId is not > 0) return new(null, null, error);
        if (await ValidateSelectionCoreAsync(defaultId, null, ownIds, ct) is { } defaultError)
            throw new SellerSelectionException(defaultError);
        return new(defaultId, null, error);
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
        return await CompanyBrandImageVersionStore.GetOrCreateAsync(_db, companyId, kind, hash, dataUrl, ct);
    }

    private async Task<LegalEntity?> LoadCompanyAsync(int id, LegalEntity? defaultCompany, CancellationToken ct)
        => defaultCompany is not null && defaultCompany.Id == id
            ? defaultCompany
            : await _db.LegalEntities.AsNoTracking().Include(le => le.Addresses).FirstOrDefaultAsync(le => le.Id == id, ct);

    private Task<HashSet<int>> LoadOwnCompanyIdsAsync(CancellationToken ct)
        => OwnCompanyRegistry.LoadIdsAsync(_db, ct);
}
