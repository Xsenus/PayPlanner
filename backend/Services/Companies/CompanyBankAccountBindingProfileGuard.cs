using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

/// <summary>Common save/import protection; JSON fields cannot create or replace a server confirmation.</summary>
public static class CompanyBankAccountBindingProfileGuard
{
    private sealed record BindingIdentity(int Company, string ProfileId, string Key, string Number, string Bik, string Currency);

    public static async Task ValidateTrackedAsync(PaymentContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries<LegalEntity>()
            .Where(x => x.State == EntityState.Modified && x.Entity.Id > 0 && x.Property(c => c.BankAccountsJson).IsModified)
            .OrderBy(x => x.Entity.Id).Select(x => (x.Entity.Id, x.Entity.BankAccountsJson)).ToArray();
        if (changes.Length == 0) return;
        if (db.Database.CurrentTransaction is null) throw new SellerSnapshotConflictException();
        // Database merge can update many companies at once. Batch locks and reads
        // instead of introducing a query pair for every imported company.
        foreach (var chunk in changes.Chunk(16))
        {
            var ids = chunk.Select(x => x.Id).ToArray();
            if (db.Database.IsNpgsql())
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT").ToArrayAsync(ct);
            var bindings = await ReadBindingsAsync(db, ids, ct);
            var groups = bindings.ToLookup(x => x.Company);
            foreach (var (id, json) in chunk) ValidateCandidate(json, groups[id].ToArray());
        }
    }

    public static async Task ValidateAsync(PaymentContext db, int companyId, string? candidateJson, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new SellerSnapshotConflictException();
        // Retain this lock through the writer's SaveChanges AND outer commit.
        // In Serializable PostgreSQL, a stale snapshot fails rather than hiding
        // a confirmation committed by another writer. SQLite retains its writer lease.
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
        var bindings = await ReadBindingsAsync(db, [companyId], ct);
        ValidateCandidate(candidateJson, bindings);
    }

    private static async Task<BindingIdentity[]> ReadBindingsAsync(PaymentContext db, int[] ids, CancellationToken ct)
    {
        var limit = ids.Length * CompanyBankAccountMatchService.MaximumProfileAccounts;
        var rows = await db.CompanyBankAccountBindings.AsNoTracking().Where(x => ids.Contains(x.LegalEntityId) && x.RevokedAtUtc == null)
            .OrderBy(x => x.LegalEntityId).ThenBy(x => x.Id).Take(limit + 1)
            .Select(x => new BindingIdentity(x.LegalEntityId, x.ProfileAccountId.Length <= CompanyBankAccountList.MaxJsonLength ? x.ProfileAccountId : "", x.ProfileAccountKey, x.AccountNumber, x.Bik, x.Currency)).ToArrayAsync(ct);
        if (rows.Length > limit) throw Protected();
        return rows;
    }

    private static void ValidateCandidate(string? candidateJson, BindingIdentity[] bindings)
    {
        if (bindings.Length == 0) return;
        if (bindings.Length > CompanyBankAccountMatchService.MaximumProfileAccounts || candidateJson?.Length > CompanyBankAccountList.MaxJsonLength)
            throw Protected();
        var issues = new List<string>();
        CompanyBankAccountMatchService.Profile[] profiles;
        try { profiles = CompanyBankAccountMatchService.ReadProfiles(candidateJson, issues); }
        catch (CompanyBankAccountMatchLimitException) { throw Protected(); }
        // Existing import rows may append an unrelated, not-yet-configured account.
        // Only confirmed IDs must be present and retain their exact identity.
        if (issues.Any(x => x is "InvalidProfileAccounts" or "DuplicateProfileId")) throw Protected();
        var byId = profiles.Where(x => x.Id is not null).ToDictionary(x => x.Id!, StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var identity = CompanyBankAccountMatchService.Key(binding.Number, binding.Bik, binding.Currency);
            if (string.IsNullOrWhiteSpace(binding.ProfileId) || identity is null
                || binding.Key != CompanyBankAccountBindingIdentity.ProfileKey(binding.ProfileId)
                || !byId.TryGetValue(binding.ProfileId, out var profile)
                || profile.Identity != identity) throw Protected();
        }
    }

    private static CompanyBankBindingException Protected() => new("ProfileBindingProtected",
        "У подтверждённого банковского счёта нельзя удалить или заменить идентификатор, номер, БИК или валюту. Сначала явно снимите связь с банком.");
}
