using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

public sealed class CompanyBankBindingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CompanyBankBindingReceipt(int BindingId, string State, bool IsReplay);

/// <summary>
/// Administrative commands shared by HTTP and profile/import identity guards.
/// Never rewrites money accounts or documents.
/// </summary>
public sealed class CompanyBankAccountBindingService(PaymentContext db)
{
    public Task<CompanyBankBindingReceipt> BindAsync(ClaimsPrincipal principal, int connectionId, int bankId, string profileId, string expectedVersion, CancellationToken ct)
        => RunAsync(principal, connectionId, expectedVersion, async (companyId, actor) =>
        {
            if (bankId <= 0 || string.IsNullOrWhiteSpace(profileId) || profileId.Length > CompanyBankAccountList.MaxJsonLength)
                throw Invalid("Укажите банковский и локальный счёт.");
            await LockBankSourceAsync(connectionId, bankId, ct);
            var bank = await db.BankConnectionAccounts.AsNoTracking().Where(x => x.Id == bankId && x.BankConnectionId == connectionId)
                .Select(x => new { x.Id, x.AccountNumber, x.Bik, x.Currency, x.ExternalAccountId, x.Status, x.IsHidden, x.BankConnection.Provider, ConnectionStatus = x.BankConnection.Status }).SingleOrDefaultAsync(ct);
            if (bank is null) throw Missing();
            if (bank.ConnectionStatus != BankConnectionStatuses.Active || bank.IsHidden || !BankAccountStatuses.AllowsNewDocuments(bank.Status))
                throw Invalid("Банковский счёт сейчас недоступен для новой связи.");
            if (bank.Provider is not (BankProviderKeys.Sber or BankProviderKeys.Tochka)
                || string.IsNullOrWhiteSpace(bank.ExternalAccountId) || bank.ExternalAccountId.Length > 128)
                throw Invalid("Не удалось подтвердить источник банковского счёта.");
            var identity = CompanyBankAccountMatchService.Key(bank.AccountNumber, bank.Bik, bank.Currency);
            if (identity is null || identity.Number.Length > 34 || identity.Bik.Length > 16 || identity.Currency.Length > 8)
                throw Invalid("Для связи нужны полный номер счёта, БИК и валюта.");
            var json = await db.LegalEntities.AsNoTracking().Where(x => x.Id == companyId)
                .Select(x => x.BankAccountsJson == null || x.BankAccountsJson.Length <= CompanyBankAccountList.MaxJsonLength ? x.BankAccountsJson : null).SingleAsync(ct);
            var issues = new List<string>();
            var profiles = CompanyBankAccountMatchService.ReadProfiles(json, issues);
            if (issues.Any(x => x is "InvalidProfileAccounts" or "DuplicateProfileId" or "MissingStableProfileId"))
                throw Invalid("Уточните стабильные идентификаторы счетов профиля.");
            var profile = profiles.SingleOrDefault(x => string.Equals(x.Id, profileId, StringComparison.Ordinal));
            if (profile is null || profile.Archived || profile.Identity != identity)
                throw Invalid("Выбранный счёт профиля не совпадает с реквизитами банка или находится в архиве.");
            var report = await ReportAsync(connectionId, ct);
            var row = report.Accounts.Single(x => x.BankAccountId == bankId);
            if (row.Issues.Any(x => x is "LinkedMoneyIdentityMismatch" or "LinkedMoneyAccountInactive" or "MultipleLinkedMoneyAccounts"))
                throw Invalid("Связанный денежный счёт требует проверки владельца и реквизитов.");
            var key = CompanyBankAccountBindingIdentity.ProfileKey(profile.Id!);
            var active = await db.CompanyBankAccountBindings.AsNoTracking()
                .Where(x => x.RevokedAtUtc == null && (x.LegalEntityId == companyId && x.ProfileAccountKey == key || x.BankConnectionAccountId == bankId))
                .Take(3).ToArrayAsync(ct);
            if (active.Length == 1 && active[0] is var known && SameConfirmation(known, companyId, bankId, profileId, bank.Provider, bank.ExternalAccountId, identity)
                && string.Equals(known.ConfirmedMatchVersion, expectedVersion, StringComparison.Ordinal)
                && string.Equals((await ReportAsync(connectionId, ct, known.Id)).Version, expectedVersion, StringComparison.Ordinal))
                return new(known.Id, "Bound", true);
            if (!string.Equals(report.Version, expectedVersion, StringComparison.Ordinal)) throw Changed();
            if (active.Length != 0) throw Invalid("Локальный или банковский счёт уже связан. Сначала явно снимите прежнюю связь.");
            await TouchCompanyAsync(companyId, ct);
            var binding = new CompanyBankAccountBinding
            {
                LegalEntityId = companyId,
                ProfileAccountId = profile.Id!,
                ProfileAccountKey = key,
                BankConnectionAccountId = bankId,
                Provider = bank.Provider,
                ExternalAccountId = bank.ExternalAccountId,
                AccountNumber = identity.Number,
                Bik = identity.Bik,
                Currency = identity.Currency,
                ConfirmedMatchVersion = expectedVersion,
                CreatedByUserId = actor.Id,
                CreatedByUserName = actor.Name,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CompanyBankAccountBindings.Add(binding);
            await db.SaveChangesAsync(ct);
            return new(binding.Id, "Bound", false);
        }, true, ct);

    public Task<CompanyBankBindingReceipt> RevokeAsync(ClaimsPrincipal principal, int connectionId, int bindingId, string expectedVersion, CancellationToken ct)
        => RunAsync(principal, connectionId, expectedVersion, async (companyId, actor) =>
        {
            if (bindingId <= 0) throw Invalid("Укажите подтверждение связи.");
            if (db.Database.IsNpgsql())
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBankAccountBindings\" WHERE \"Id\" = {bindingId} AND \"LegalEntityId\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
            var binding = await db.CompanyBankAccountBindings.SingleOrDefaultAsync(x => x.Id == bindingId && x.LegalEntityId == companyId, ct);
            if (binding is null) throw Missing();
            if (binding.RevokedAtUtc.HasValue)
            {
                if (string.Equals(binding.RevocationMatchVersion, expectedVersion, StringComparison.Ordinal)) return new(binding.Id, "Revoked", true);
                throw Changed();
            }
            var report = await ReportAsync(connectionId, ct);
            if (!string.Equals(report.Version, expectedVersion, StringComparison.Ordinal)) throw Changed();
            await TouchCompanyAsync(companyId, ct);
            // Removing this company's own confirmation remains possible after bank
            // closure/identity drift. No bank or money record is changed or reassigned.
            var now = DateTime.UtcNow;
            binding.RevokedAtUtc = now < binding.CreatedAtUtc ? binding.CreatedAtUtc : now;
            binding.RevokedByUserId = actor.Id; binding.RevokedByUserName = actor.Name;
            binding.RevocationMatchVersion = expectedVersion;
            await db.SaveChangesAsync(ct);
            return new(binding.Id, "Revoked", false);
        }, false, ct);

    private sealed record Actor(int Id, string Name);

    private async Task<CompanyBankBindingReceipt> RunAsync(ClaimsPrincipal principal, int connectionId, string expectedVersion,
        Func<int, Actor, Task<CompanyBankBindingReceipt>> action, bool requireBindableCompany, CancellationToken ct)
    {
        if (!ValidVersion(expectedVersion)) throw Changed();
        try
        {
            return await DocumentSellerLease.RunSerializableAsync(db, async lease =>
            {
                lease.AdmitCleanTracker();
                if (principal.Identity?.IsAuthenticated != true || principal.GetUserId() is not > 0) throw Denied();
                var id = principal.GetUserId()!.Value;
                if (db.Database.IsNpgsql())
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
                    await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"Id\" = {id} FOR SHARE NOWAIT").ToArrayAsync(ct);
                    await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {id}) FOR SHARE NOWAIT").ToArrayAsync(ct);
                }
                if (!await db.IsApprovedUserAdminAsync(principal, ct)) throw Denied();
                if (!AuthSessionClaims.TryGetTokenVersion(principal, out var tokenVersion)) throw Denied();
                var actor = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.FullName, x.AuthTokenVersion }).SingleAsync(ct);
                if (actor.AuthTokenVersion != tokenVersion) throw Denied();
                await lease.LockRegistryAsync(ct);
                var companyId = await db.BankConnections.AsNoTracking().Where(x => x.Id == connectionId).Select(x => (int?)x.LegalEntityId).SingleOrDefaultAsync(ct);
                if (!companyId.HasValue) throw Missing();
                if (requireBindableCompany && !(await OwnCompanyRegistry.LoadIdsAsync(db, ct)).Contains(companyId.Value)) throw Missing();
                await lease.LockCompanyRowAsync(companyId.Value, ct);
                if (db.Database.IsNpgsql())
                    await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnections\" WHERE \"Id\" = {connectionId} FOR SHARE NOWAIT").ToArrayAsync(ct);
                // Discard clean cached entities only after admission. All authority is
                // read from the locked database, never from principal role/name claims.
                db.ChangeTracker.Clear();
                if (requireBindableCompany && !await db.LegalEntities.AsNoTracking().AnyAsync(x => x.Id == companyId.Value && x.ProfileStatus == CompanyProfileStatuses.Active, ct))
                    throw Missing();
                return await action(companyId.Value, new(id, actor.FullName.Length <= 200 ? actor.FullName : actor.FullName[..200]));
            }, static _ => true, ct);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error)) { throw Changed(); }
    }

    private async Task LockBankSourceAsync(int connectionId, int bankId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        var lockedBank = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"BankConnectionAccounts\" WHERE \"Id\" = {bankId} AND \"BankConnectionId\" = {connectionId} FOR SHARE NOWAIT").ToArrayAsync(ct);
        if (lockedBank.Length == 0) return;
        await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"MoneyAccounts\" WHERE \"BankConnectionAccountId\" = {bankId} ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);
    }

    private async Task TouchCompanyAsync(int companyId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        // A profile/import writer may have read its Serializable snapshot before
        // this confirmation existed. Advance the company's MVCC row version while
        // preserving every business field, so its later row lock cannot miss us.
        if (await db.LegalEntities.Where(x => x.Id == companyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ShortName, x => x.ShortName), ct) != 1) throw Missing();
    }

    private async Task<CompanyBankAccountMatchReport> ReportAsync(int connectionId, CancellationToken ct, int? exclude = null)
        => await new CompanyBankAccountMatchService(db).ReadCoreAsync(connectionId, ct, exclude) ?? throw Missing();

    private static bool SameConfirmation(CompanyBankAccountBinding b, int company, int bank, string profileId, string provider, string externalId, CompanyBankAccountMatchService.Identity key)
        => b.LegalEntityId == company && b.BankConnectionAccountId == bank && b.ProfileAccountId == profileId
            && b.ProfileAccountKey == CompanyBankAccountBindingIdentity.ProfileKey(profileId) && b.Provider == provider && b.ExternalAccountId == externalId
            && b.AccountNumber == key.Number && b.Bik == key.Bik && b.Currency == key.Currency;

    private static bool ValidVersion(string? version)
        => version is not null && version.StartsWith("bank-profile-match-v2:", StringComparison.Ordinal)
            && version.Length == "bank-profile-match-v2:".Length + 64 && version.AsSpan("bank-profile-match-v2:".Length).IndexOfAnyExcept("0123456789ABCDEF") < 0;

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                || e is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 }) return true;
        return false;
    }
    private static CompanyBankBindingException Denied() => new("Forbidden", "Подтвердить или снять связь может только действующий одобренный администратор.");
    private static CompanyBankBindingException Missing() => new("NotFound", "Счёт или профиль компании недоступен.");
    private static CompanyBankBindingException Changed() => new("Changed", "Счета или связь изменились. Обновите сверку и повторите выбор.");
    private static CompanyBankBindingException Invalid(string message) => new("InvalidSelection", message);
}
