using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Banking;

public sealed record BankAccountsSyncResult(int Added, int Updated, int Skipped, string? Error);

public sealed record BankStatementSyncResult(int Inserted, int Updated, DateTime? Watermark, string? Error);

public interface IBankSyncService
{
    Task<BankAccountsSyncResult> SyncAccountsAsync(int connectionId, CancellationToken ct);

    Task<BankStatementSyncResult> SyncStatementAsync(int accountId, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Загружает выписки всех разрешённых счетов подключения. Ошибка одного счёта не останавливает остальные.</summary>
    Task<IReadOnlyDictionary<int, BankStatementSyncResult>> SyncAllStatementsAsync(int connectionId, DateTime to, CancellationToken ct);
}

/// <summary>
/// Общая часть работы с банками: загрузка счетов и выписок без дублей и потерь, независимо от адаптера.
/// Правила: счёт никогда не удаляется и не закрывается из-за отсутствия в ответе; отметка «загружено до»
/// сдвигается только после подтверждённой записи операций; ошибка банка фиксируется на счёте или подключении.
/// </summary>
public sealed class BankSyncService : IBankSyncService
{
    private const int MaxPages = 500;
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromDays(3);

    private readonly PaymentContext _db;
    private readonly IReadOnlyDictionary<string, IBankProvider> _providers;
    private readonly IDataProtector _protector;

    public BankSyncService(PaymentContext db, IEnumerable<IBankProvider> providers, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _providers = providers.ToDictionary(item => item.Key, StringComparer.Ordinal);
        _protector = dataProtection.CreateProtector("PayPlanner.BankConnections.Tokens.v1");
    }

    /// <summary>Сохраняет токены подключения в защищённом виде.</summary>
    public void StoreTokens(BankConnection connection, string accessToken, string? refreshToken, DateTime? expiresAt)
    {
        connection.AccessTokenProtected = _protector.Protect(accessToken);
        if (refreshToken is not null)
        {
            connection.RefreshTokenProtected = _protector.Protect(refreshToken);
        }

        connection.AccessTokenExpiresAt = expiresAt;
        connection.Status = BankConnectionStatuses.Active;
        connection.LastError = null;
        connection.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<BankAccountsSyncResult> SyncAccountsAsync(int connectionId, CancellationToken ct)
    {
        var connection = await _db.BankConnections.FirstOrDefaultAsync(item => item.Id == connectionId, ct);
        if (connection is null)
        {
            return new BankAccountsSyncResult(0, 0, 0, "Подключение не найдено.");
        }

        var (provider, credentials, setupError) = Prepare(connection);
        if (provider is null || credentials is null)
        {
            return new BankAccountsSyncResult(0, 0, 0, setupError);
        }

        IReadOnlyList<BankAccountInfo> remote;
        try
        {
            remote = await provider.ListAccountsAsync(credentials, ct);
        }
        catch (BankAuthorizationException ex)
        {
            return await FailConnectionAsync(connection, BankConnectionStatuses.ReauthRequired, ex.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailConnectionAsync(connection, connection.Status, ex.Message, ct);
        }

        var companyInn = await _db.LegalEntities.AsNoTracking()
            .Where(le => le.Id == connection.LegalEntityId)
            .Select(le => le.Inn)
            .FirstOrDefaultAsync(ct);

        var existing = await _db.BankConnectionAccounts
            .Where(item => item.BankConnectionId == connection.Id)
            .ToDictionaryAsync(item => item.ExternalAccountId, StringComparer.Ordinal, ct);

        int added = 0, updated = 0, skipped = 0;
        foreach (var info in remote)
        {
            // Чужой ИНН владельца — счёт другой компании: не привязываем.
            if (!string.IsNullOrWhiteSpace(info.OwnerInn) && !string.IsNullOrWhiteSpace(companyInn)
                && !string.Equals(info.OwnerInn.Trim(), companyInn.Trim(), StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var status = BankAccountStatuses.All.Contains(info.Status) ? info.Status : BankAccountStatuses.Unavailable;
            if (existing.TryGetValue(info.ExternalId, out var account))
            {
                var changed = account.AccountNumber != info.AccountNumber || account.Bik != info.Bik
                    || account.Currency != info.Currency || account.Status != status;
                if (changed)
                {
                    account.AccountNumber = info.AccountNumber;
                    account.Bik = info.Bik;
                    account.Currency = info.Currency;
                    account.Status = status;
                    account.UpdatedAt = DateTime.UtcNow;
                    updated++;
                }
            }
            else
            {
                var created = new BankConnectionAccount
                {
                    BankConnectionId = connection.Id,
                    ExternalAccountId = info.ExternalId,
                    AccountNumber = info.AccountNumber,
                    Bik = info.Bik,
                    Currency = info.Currency,
                    Status = status,
                };
                _db.BankConnectionAccounts.Add(created);
                existing[info.ExternalId] = created;
                added++;
            }
        }

        connection.LastAccountsSyncAt = DateTime.UtcNow;
        connection.LastError = null;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new BankAccountsSyncResult(added, updated, skipped, null);
    }

    public async Task<BankStatementSyncResult> SyncStatementAsync(int accountId, DateTime from, DateTime to, CancellationToken ct)
    {
        var account = await _db.BankConnectionAccounts
            .Include(item => item.BankConnection)
            .FirstOrDefaultAsync(item => item.Id == accountId, ct);
        if (account is null)
        {
            return new BankStatementSyncResult(0, 0, null, "Счёт не найден.");
        }

        if (!account.StatementsEnabled || account.Status == BankAccountStatuses.Unavailable)
        {
            return new BankStatementSyncResult(0, 0, account.StatementWatermark, "Загрузка выписки для счёта отключена или счёт недоступен.");
        }

        var (provider, credentials, setupError) = Prepare(account.BankConnection);
        if (provider is null || credentials is null)
        {
            return await FailAccountAsync(account, setupError, ct);
        }

        // Повторно берём небольшой хвост до отметки: банк мог изменить незавершённые операции.
        var start = account.StatementWatermark.HasValue && account.StatementWatermark.Value - OverlapWindow > from
            ? account.StatementWatermark.Value - OverlapWindow
            : from;

        var fetched = new Dictionary<string, BankOperationInfo>(StringComparer.Ordinal);
        try
        {
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var result = await provider.GetStatementPageAsync(credentials, account.ExternalAccountId, start, to, cursor, ct);
                foreach (var item in result.Items)
                {
                    fetched[item.ExternalId] = item;
                }

                cursor = result.NextCursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }

                if (page == MaxPages - 1)
                {
                    throw new InvalidOperationException("Выписка превысила допустимое число страниц.");
                }
            }
        }
        catch (BankAuthorizationException ex)
        {
            account.BankConnection.Status = BankConnectionStatuses.ReauthRequired;
            account.BankConnection.LastError = Trim(ex.Message);
            return await FailAccountAsync(account, ex.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAccountAsync(account, ex.Message, ct);
        }

        var ids = fetched.Keys.ToList();
        var known = await _db.BankStatementOperations
            .Where(item => item.BankConnectionAccountId == account.Id && ids.Contains(item.ExternalOperationId))
            .ToDictionaryAsync(item => item.ExternalOperationId, StringComparer.Ordinal, ct);

        int inserted = 0, updatedCount = 0;
        foreach (var info in fetched.Values)
        {
            if (known.TryGetValue(info.ExternalId, out var row))
            {
                // Подтверждённая операция больше не меняется; незавершённая обновляется.
                if (!row.IsBooked)
                {
                    Apply(row, info);
                    updatedCount++;
                }

                continue;
            }

            var created = new BankStatementOperation { BankConnectionAccountId = account.Id, ExternalOperationId = info.ExternalId };
            Apply(created, info);
            _db.BankStatementOperations.Add(created);
            inserted++;
        }

        // Отметка сдвигается только после успешного сохранения операций и не заходит за незавершённые.
        var lastBooked = fetched.Values.Where(item => item.IsBooked).Select(item => (DateTime?)item.Date).Max();
        var firstPending = fetched.Values.Where(item => !item.IsBooked).Select(item => (DateTime?)item.Date).Min();
        DateTime? newWatermark = lastBooked;
        if (newWatermark.HasValue && firstPending.HasValue && firstPending.Value <= newWatermark.Value)
        {
            newWatermark = firstPending.Value.AddTicks(-1);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Параллельная загрузка того же счёта вставила те же операции: уникальный индекс защитил от дублей.
            _db.ChangeTracker.Clear();
            return new BankStatementSyncResult(0, 0, account.StatementWatermark, Trim(ex.GetBaseException().Message));
        }

        if (newWatermark.HasValue && (!account.StatementWatermark.HasValue || newWatermark > account.StatementWatermark))
        {
            account.StatementWatermark = newWatermark;
        }

        account.LastStatementSyncAt = DateTime.UtcNow;
        account.LastError = null;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new BankStatementSyncResult(inserted, updatedCount, account.StatementWatermark, null);
    }

    public async Task<IReadOnlyDictionary<int, BankStatementSyncResult>> SyncAllStatementsAsync(int connectionId, DateTime to, CancellationToken ct)
    {
        var accountIds = await _db.BankConnectionAccounts.AsNoTracking()
            .Where(item => item.BankConnectionId == connectionId && item.StatementsEnabled && item.Status != BankAccountStatuses.Unavailable)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.CreatedAt })
            .ToListAsync(ct);

        var results = new Dictionary<int, BankStatementSyncResult>();
        foreach (var item in accountIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results[item.Id] = await SyncStatementAsync(item.Id, item.CreatedAt.AddDays(-90), to, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[item.Id] = new BankStatementSyncResult(0, 0, null, Trim(ex.Message));
            }
        }

        return results;
    }

    private (IBankProvider? Provider, BankCredentials? Credentials, string? Error) Prepare(BankConnection connection)
    {
        if (connection.Status != BankConnectionStatuses.Active)
        {
            return (null, null, "Подключение не активно: требуется повторная авторизация.");
        }

        if (!_providers.TryGetValue(connection.Provider, out var provider))
        {
            return (null, null, $"Банк {connection.Provider} пока не поддерживается.");
        }

        if (string.IsNullOrEmpty(connection.AccessTokenProtected))
        {
            return (null, null, "Для подключения не сохранён токен доступа.");
        }

        try
        {
            var access = _protector.Unprotect(connection.AccessTokenProtected);
            var refresh = string.IsNullOrEmpty(connection.RefreshTokenProtected) ? null : _protector.Unprotect(connection.RefreshTokenProtected);
            return (provider, new BankCredentials(access, refresh), null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (null, null, "Токен доступа не удалось расшифровать: подключите банк заново.");
        }
    }

    private async Task<BankAccountsSyncResult> FailConnectionAsync(BankConnection connection, string status, string message, CancellationToken ct)
    {
        connection.Status = status;
        connection.LastError = Trim(message);
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new BankAccountsSyncResult(0, 0, 0, connection.LastError);
    }

    private async Task<BankStatementSyncResult> FailAccountAsync(BankConnectionAccount account, string? message, CancellationToken ct)
    {
        account.LastError = Trim(message);
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new BankStatementSyncResult(0, 0, account.StatementWatermark, account.LastError);
    }

    private static void Apply(BankStatementOperation row, BankOperationInfo info)
    {
        row.OperationDate = info.Date;
        row.Amount = Math.Abs(info.Amount);
        row.IsIncome = info.IsIncome;
        row.IsBooked = info.IsBooked;
        row.CounterpartyInn = Trim(info.CounterpartyInn, 20);
        row.CounterpartyName = Trim(info.CounterpartyName, 300);
        row.CounterpartyAccount = Trim(info.CounterpartyAccount, 34);
        row.Purpose = Trim(info.Purpose, 2000);
    }

    private static string? Trim(string? value, int max = 1000)
        => string.IsNullOrWhiteSpace(value) ? null : (value.Length <= max ? value.Trim() : value[..max]);
}
