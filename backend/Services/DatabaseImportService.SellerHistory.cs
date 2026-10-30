using System.Security.Cryptography;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private sealed record SellerImportState(int CompanyId, int? BranchId, string? SnapshotHash, IReadOnlyList<(int CompanyId, string Kind, int VersionId)> Images);
    private sealed record SellerImportImage(int CompanyId, string Kind, string Hash);
    private static readonly (string Property, string Kind)[] SellerImageProperties =
        [("LogoVersionId", CompanyBrandImageKinds.Logo), ("SealVersionId", CompanyBrandImageKinds.Seal), ("SignatureVersionId", CompanyBrandImageKinds.Signature)];
    private static readonly Expression<Func<LegalEntity, LegalEntity>> SellerCounterpartyProjection = c => new LegalEntity
    { Id = c.Id, ShortName = c.ShortName, FullName = c.FullName, Inn = c.Inn, Kpp = c.Kpp, Ogrn = c.Ogrn, Address = c.Address, Phone = c.Phone, Email = c.Email, Director = c.Director, BankAccountsJson = c.BankAccountsJson };

    private static string? SnapshotHash(string? value) => string.IsNullOrWhiteSpace(value) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static SellerImportState InspectSellerHistory(int? company, int? branch, string? snapshot, string? policy)
    {
        if (company is not > 0 || branch is <= 0) throw new InvalidOperationException(SellerMergeUnsupported);
        if (string.IsNullOrWhiteSpace(snapshot)) return new(company.Value, branch, null, []);
        try
        {
            if (snapshot.Length > DocumentSellerService.MaxSnapshotLength) throw new InvalidOperationException(SellerMergeUnsupported);
            var json = JsonNode.Parse(snapshot)!.AsObject();
            if (json["Id"]?.GetValue<int>() != company || string.IsNullOrWhiteSpace(json["ShortName"]?.GetValue<string>()))
                throw new InvalidOperationException(SellerMergeUnsupported);
            var metadata = SellerLegalEntityComposer.ReadMetadata(snapshot) ?? throw new InvalidOperationException(SellerMergeUnsupported);
            if (metadata.FreezeReason is not null && (!DocumentSellerSnapshotPolicy.IsValid(metadata.FreezeReason)
                || policy is not null && metadata.FreezeReason != policy)) throw new InvalidOperationException(SellerMergeUnsupported);
            if (metadata.LegacyImages && SellerImageProperties.Any(p => json[p.Property] is not null)) throw new InvalidOperationException(SellerMergeUnsupported);
            return new(company.Value, branch, SnapshotHash(snapshot), SellerLegalEntityComposer.ReadFrozenImageReferences(snapshot));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or SellerSelectionException or FormatException)
        {
            throw new InvalidOperationException(SellerMergeUnsupported, error);
        }
    }

    private static async Task PlanSellerHistoryAsync(PaymentContext source, PaymentContext target, ImportMaps maps, CancellationToken ct)
    {
        if (maps.SellerHistory.Count == 0) return;
        var pendingCompanies = maps.SellerHistory.Values.Where(s => s.SnapshotHash is null).Select(s => s.CompanyId).ToHashSet();
        var pendingBranches = maps.SellerHistory.Values.Where(s => s.SnapshotHash is null && s.BranchId.HasValue).Select(s => s.BranchId!.Value).ToHashSet();
        var branchOwners = maps.SellerHistory.Values.Where(s => s.BranchId.HasValue).GroupBy(s => s.BranchId!.Value).ToDictionary(g => g.Key, g => g.Select(s => s.CompanyId).ToHashSet());
        var ids = maps.SellerHistory.Values.Select(s => s.CompanyId).Distinct().ToArray();
        var companies = new Dictionary<int, LegalEntity>();
        foreach (var batch in ids.Chunk(128))
            foreach (var company in await source.LegalEntities.AsNoTracking().Where(c => batch.Contains(c.Id))
                .Select(c => new LegalEntity { Id = c.Id, ShortName = c.ShortName, Inn = c.Inn, Kpp = c.Kpp, Ogrn = c.Ogrn, BankAccountsJson = c.BankAccountsJson }).ToArrayAsync(ct))
                companies.Add(company.Id, company);
        if (companies.Count != ids.Length || companies.Values.Any(c => string.IsNullOrWhiteSpace(c.Inn) && string.IsNullOrWhiteSpace(c.Ogrn))
            || companies.Values.GroupBy(BuildLegalEntityKey).Any(g => g.Count() != 1)) throw new InvalidOperationException(SellerMergeUnsupported);
        var registry = await OwnCompanyRegistry.ReadStateAsync(target, ct);
        var own = registry.OwnIds();
        var inns = companies.Values.Select(c => NormalizeText(c.Inn)).Where(s => s.Length > 0).Distinct().ToArray();
        var ogrns = companies.Values.Select(c => NormalizeText(c.Ogrn)).Where(s => s.Length > 0).Distinct().ToArray();
        var targets = await target.LegalEntities.IgnoreQueryFilters().AsNoTracking()
            .Where(c => inns.Contains((c.Inn ?? "").Trim().ToUpper()) || ogrns.Contains((c.Ogrn ?? "").Trim().ToUpper()))
            .Select(c => new LegalEntity { Id = c.Id, ShortName = c.ShortName, Inn = c.Inn, Kpp = c.Kpp, Ogrn = c.Ogrn, ProfileStatus = c.ProfileStatus, BankAccountsJson = c.BankAccountsJson }).ToArrayAsync(ct);
        var liveTargetIds = (await target.LegalEntities.AsNoTracking().Where(c => own.Contains(c.Id)).Select(c => c.Id).ToArrayAsync(ct)).ToHashSet();
        foreach (var company in companies.Values)
        {
            var matches = targets.Where(c => BuildLegalEntityKey(c) == BuildLegalEntityKey(company)).ToArray();
            if (matches.Length != 1 || !own.Contains(matches[0].Id) || !liveTargetIds.Contains(matches[0].Id)
                || matches[0].ProfileStatus != CompanyProfileStatuses.Active && pendingCompanies.Contains(company.Id))
                throw new InvalidOperationException(SellerMergeUnsupported);
            maps.SellerCompanies.Add(company.Id, matches[0].Id);
        }
        // Reuse the same registry/company locks as snapshot capture. They live until the outer import commits.
        await DocumentSellerLease.RunAsync(target, async lease =>
        {
            await lease.LockRegistryAsync(ct);
            foreach (var companyId in maps.SellerCompanies.Values.Order()) await lease.LockCompanyAsync(companyId, null, ct);
            if (await OwnCompanyRegistry.ReadStateAsync(target, ct) != registry) throw new InvalidOperationException(SellerMergeUnsupported);
            return true;
        }, ct);

        var branchIds = maps.SellerHistory.Values.Where(s => s.BranchId.HasValue).Select(s => s.BranchId!.Value).Distinct().ToArray();
        if (branchIds.Length > 0)
        {
            if ((await GetSqliteTableColumnsAsync(source.Database.GetDbConnection(), "CompanyBranches", ct)).Count == 0) throw new InvalidOperationException(SellerMergeUnsupported);
            var targetIds = maps.SellerCompanies.Values.Distinct().ToArray();
            var targetBranches = await target.CompanyBranches.IgnoreQueryFilters().AsNoTracking().Where(b => targetIds.Contains(b.LegalEntityId)).ToArrayAsync(ct);
            foreach (var batch in branchIds.Chunk(128))
            {
                var branches = await source.CompanyBranches.AsNoTracking().Where(b => batch.Contains(b.Id)).ToArrayAsync(ct);
                if (branches.Length != batch.Length) throw new InvalidOperationException(SellerMergeUnsupported);
                foreach (var branch in branches)
                {
                    if (!maps.SellerCompanies.TryGetValue(branch.LegalEntityId, out var companyId)
                        || branchOwners[branch.Id].Count != 1 || !branchOwners[branch.Id].Contains(branch.LegalEntityId)
                        || CompanyBranchKinds.Normalize(branch.Kind) != branch.Kind || CompanyProfileStatuses.NormalizeBranch(branch.Status) != branch.Status
                        || string.IsNullOrWhiteSpace(branch.Name)) throw new InvalidOperationException(SellerMergeUnsupported);
                    var pending = pendingBranches.Contains(branch.Id);
                    if (pending && branch.Status != CompanyProfileStatuses.Active) throw new InvalidOperationException(SellerMergeUnsupported);
                    var account = MapSellerBankAccount(companies[branch.LegalEntityId], targets.Single(c => c.Id == companyId), branch.BankAccountId, pending);
                    var planned = new CompanyBranch
                    {
                        LegalEntityId = companyId,
                        Name = branch.Name,
                        Kind = branch.Kind,
                        Code = branch.Code,
                        Status = branch.Status,
                        Kpp = branch.Kpp,
                        Address = branch.Address,
                        SignerJson = branch.SignerJson,
                        BankAccountId = account,
                        Notes = branch.Notes,
                        CreatedAt = branch.CreatedAt,
                        UpdatedAt = branch.UpdatedAt
                    };
                    var matches = targetBranches.Where(b => b.LegalEntityId == companyId && BranchHistoryKey(b) == BranchHistoryKey(planned)).ToArray();
                    if (matches.Length > 1 || matches.Length == 0 && targetBranches.Any(b => b.LegalEntityId == companyId
                        && (Key(b.Kind, b.Name) == Key(planned.Kind, planned.Name) || !string.IsNullOrWhiteSpace(branch.Code) && Key(b.Code) == Key(branch.Code))))
                        throw new InvalidOperationException(SellerMergeUnsupported);
                    if (matches.Length == 1) maps.SellerBranches.Add(branch.Id, matches[0].Id);
                    else maps.PlannedSellerBranches.Add(branch.Id, planned);
                }
            }
            if (maps.PlannedSellerBranches.Values.GroupBy(b => Key(b.LegalEntityId, BranchHistoryKey(b))).Any(g => g.Count() > 1))
                throw new InvalidOperationException(SellerMergeUnsupported);
            if (maps.PlannedSellerBranches.Values.GroupBy(b => Key(b.LegalEntityId, b.Kind, b.Name)).Any(g => g.Count() > 1)
                || maps.PlannedSellerBranches.Values.Where(b => !string.IsNullOrWhiteSpace(b.Code)).GroupBy(b => Key(b.LegalEntityId, b.Code)).Any(g => g.Count() > 1))
                throw new InvalidOperationException(SellerMergeUnsupported);
        }

        foreach (var state in maps.SellerHistory.Values)
            foreach (var reference in state.Images)
            {
                var image = new SellerImportImage(reference.CompanyId, reference.Kind, "");
                if (maps.SellerImages.TryGetValue(reference.VersionId, out var previous) && previous != image) throw new InvalidOperationException(SellerMergeUnsupported);
                maps.SellerImages[reference.VersionId] = image;
            }
        if (maps.SellerImages.Count > 0)
        {
            if ((await GetSqliteTableColumnsAsync(source.Database.GetDbConnection(), "CompanyBrandImageVersions", ct)).Count == 0) throw new InvalidOperationException(SellerMergeUnsupported);
            foreach (var batch in maps.SellerImages.Keys.ToArray().Chunk(64))
            {
                var images = await source.CompanyBrandImageVersions.AsNoTracking().Where(i => batch.Contains(i.Id) && i.DataUrl.Length <= CompanyImageUploadValidator.MaxDataUrlLength).ToArrayAsync(ct);
                if (images.Length != batch.Length) throw new InvalidOperationException(SellerMergeUnsupported);
                foreach (var image in images)
                {
                    var expected = maps.SellerImages[image.Id];
                    if (image.LegalEntityId != expected.CompanyId || image.Kind != expected.Kind || string.IsNullOrWhiteSpace(image.DataUrl)
                        || image.DataUrl.Length > CompanyImageUploadValidator.MaxDataUrlLength
                        || !string.Equals(SnapshotHash(image.DataUrl), image.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(SellerMergeUnsupported);
                    maps.SellerImages[image.Id] = expected with { Hash = SnapshotHash(image.DataUrl)! };
                }
            }
        }
        var sourceUsers = await source.Users.AsNoTracking().Select(u => new { u.Id, u.Email }).ToArrayAsync(ct);
        var targetUsers = await target.Users.AsNoTracking().Select(u => new { u.Id, u.Email }).ToArrayAsync(ct);
        foreach (var user in sourceUsers)
        {
            var matches = targetUsers.Where(u => NormalizeEmail(u.Email) == NormalizeEmail(user.Email)).ToArray();
            if (string.IsNullOrWhiteSpace(user.Email) || matches.Length != 1) throw new InvalidOperationException(SellerMergeUnsupported);
            maps.Users[user.Id] = matches[0].Id;
        }
        // A merge must not change a destination counterparty through a shared email or requisites key.
        var targetClients = (await target.Clients.AsNoTracking().Select(c => new Client { Name = c.Name, Email = c.Email, Phone = c.Phone, Company = c.Company, Address = c.Address }).ToArrayAsync(ct)).GroupBy(BuildClientKey).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var client in await source.Clients.AsNoTracking().Select(c => new Client { Name = c.Name, Email = c.Email, Phone = c.Phone, Company = c.Company, Address = c.Address }).ToArrayAsync(ct))
            if (targetClients.TryGetValue(BuildClientKey(client), out var matches) && (matches.Length != 1
                || Key(client.Name, client.Email, client.Phone, client.Company, client.Address) != Key(matches[0].Name, matches[0].Email, matches[0].Phone, matches[0].Company, matches[0].Address)))
                throw new InvalidOperationException(SellerMergeUnsupported);
        var otherSourceCompanies = await source.LegalEntities.AsNoTracking().Where(c => !ids.Contains(c.Id)).Select(SellerCounterpartyProjection).ToArrayAsync(ct);
        if (otherSourceCompanies.Length > 0)
        {
            var otherTargets = (await target.LegalEntities.AsNoTracking().Select(SellerCounterpartyProjection).ToArrayAsync(ct)).GroupBy(BuildLegalEntityKey).ToDictionary(g => g.Key, g => g.ToArray());
            string PrintedCompany(LegalEntity c) => Key(c.ShortName, c.FullName, c.Inn, c.Kpp, c.Ogrn, c.Address, c.Phone, c.Email, c.Director, c.BankAccountsJson);
            foreach (var company in otherSourceCompanies)
                if (otherTargets.TryGetValue(BuildLegalEntityKey(company), out var matches) && (matches.Length != 1 || PrintedCompany(company) != PrintedCompany(matches[0])))
                    throw new InvalidOperationException(SellerMergeUnsupported);
        }
    }

    private static string BranchHistoryKey(CompanyBranch b) => Key(b.Kind, b.Code, b.Name, b.Status, b.Kpp, b.Address, b.BankAccountId, b.SignerJson);

    private static string? MapSellerBankAccount(LegalEntity source, LegalEntity target, string? id, bool requiresActive)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            JsonObject[] Accounts(string? value) => value is not null && value.Length <= CompanyBankAccountList.MaxJsonLength
                ? JsonNode.Parse(value)!.AsArray().Select(n => n!.AsObject()).ToArray() : throw new InvalidOperationException(SellerMergeUnsupported);
            string? Text(JsonObject a, string field) => a[field]?.GetValue<string>()?.Trim();
            string AccountKey(JsonObject a) => !string.IsNullOrWhiteSpace(Text(a, "accountNumber")) && !string.IsNullOrWhiteSpace(Text(a, "bik"))
                ? Key(Text(a, "accountNumber"), Text(a, "bik"), Text(a, "currency")) : throw new InvalidOperationException(SellerMergeUnsupported);
            var sourceAccounts = Accounts(source.BankAccountsJson).Where(a => Text(a, "id") == id).ToArray();
            if (sourceAccounts.Length != 1) throw new InvalidOperationException(SellerMergeUnsupported);
            var targets = Accounts(target.BankAccountsJson);
            var matches = targets.Where(a => AccountKey(a) == AccountKey(sourceAccounts[0])).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(Text(matches[0], "id"))
                || targets.Count(a => Text(a, "id") == Text(matches[0], "id")) != 1) throw new InvalidOperationException(SellerMergeUnsupported);
            if (requiresActive && (matches[0]["isArchived"]?.GetValue<bool>() == true || sourceAccounts[0]["isArchived"]?.GetValue<bool>() == true))
                throw new InvalidOperationException(SellerMergeUnsupported);
            return Text(matches[0], "id");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidOperationException(SellerMergeUnsupported, error); }
    }

    private static async Task MaterializeSellerHistoryAsync(PaymentContext source, PaymentContext target, ImportMaps maps, CancellationToken ct)
    {
        if (maps.SellerHistory.Count == 0) return;
        if (maps.SellerCompanies.Any(p => maps.LegalEntities.GetValueOrDefault(p.Key) != p.Value)) throw new InvalidOperationException(SellerMergeUnsupported);
        foreach (var branch in maps.PlannedSellerBranches.Values) target.CompanyBranches.Add(branch);
        if (maps.PlannedSellerBranches.Count > 0) await SaveImportedChangesAsync(target, ct);
        foreach (var (sourceId, branch) in maps.PlannedSellerBranches) maps.SellerBranches.Add(sourceId, branch.Id);
        foreach (var batch in maps.SellerImages.Keys.ToArray().Chunk(64))
        {
            var images = await source.CompanyBrandImageVersions.AsNoTracking().Where(i => batch.Contains(i.Id)).ToArrayAsync(ct);
            foreach (var image in images)
            {
                var expected = maps.SellerImages[image.Id];
                if (SnapshotHash(image.DataUrl) != expected.Hash) throw new InvalidOperationException(SellerMergeUnsupported);
                var company = maps.SellerCompanies[expected.CompanyId];
                var id = await CompanyBrandImageVersionStore.GetOrCreateAsync(target, company, expected.Kind, expected.Hash, image.DataUrl, ct);
                maps.SellerImageIds.Add(image.Id, id);
            }
            var ids = batch.Select(id => maps.SellerImageIds[id]).Distinct().ToArray();
            var copied = await target.CompanyBrandImageVersions.AsNoTracking().Where(i => ids.Contains(i.Id) && i.DataUrl.Length <= CompanyImageUploadValidator.MaxDataUrlLength).ToDictionaryAsync(i => i.Id, ct);
            if (copied.Count != ids.Length) throw new InvalidOperationException(SellerMergeUnsupported);
            foreach (var image in images)
            {
                var stored = copied[maps.SellerImageIds[image.Id]];
                if (stored.LegalEntityId != maps.SellerCompanies[image.LegalEntityId] || stored.Kind != image.Kind || stored.DataUrl != image.DataUrl)
                    throw new InvalidOperationException(SellerMergeUnsupported);
            }
        }
    }
}
