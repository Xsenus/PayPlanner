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

        var (provider, credentials, setupError) = await PrepareAsync(connection, ct);
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
            var externalId = Trim(info.ExternalId, 128);
            var number = Trim(info.AccountNumber, 34);
            // Без устойчивого идентификатора или номера счёт нельзя сопоставить повторно: пропускаем, а не искажаем.
            if (externalId is null || number is null || info.ExternalId.Trim().Length > 128 || info.AccountNumber.Trim().Length > 34)
            {
                skipped++;
                continue;
            }

            // Чужой ИНН владельца — счёт другой компании: не привязываем.
            if (!string.IsNullOrWhiteSpace(info.OwnerInn) && !string.IsNullOrWhiteSpace(companyInn)
                && !string.Equals(info.OwnerInn.Trim(), companyInn.Trim(), StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var status = BankAccountStatuses.All.Contains(info.Status) ? info.Status : BankAccountStatuses.Unavailable;
            var bik = Trim(info.Bik, 16);
            var currency = Trim(info.Currency, 8) ?? "RUB";
            if (existing.TryGetValue(externalId, out var account))
            {
                var changed = account.AccountNumber != number || account.Bik != bik
                    || account.Currency != currency || account.Status != status;
                if (changed)
                {
                    account.AccountNumber = number;
                    account.Bik = bik;
                    account.Currency = currency;
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
                    ExternalAccountId = externalId,
                    AccountNumber = number,
                    Bik = bik,
                    Currency = currency,
                    Status = status,
                };
                _db.BankConnectionAccounts.Add(created);
                existing[externalId] = created;
                added++;
            }
        }

        connection.LastAccountsSyncAt = DateTime.UtcNow;
        connection.LastError = null;
        connection.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Параллельная загрузка тех же счетов: уникальный индекс защитил от дублей, результат даст соседний запуск.
            _db.ChangeTracker.Clear();
            return new BankAccountsSyncResult(0, 0, skipped, Trim(ex.GetBaseException().Message));
        }

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

        var (provider, credentials, setupError) = await PrepareAsync(account.BankConnection, ct);
        if (provider is null || credentials is null)
        {
            return await FailAccountAsync(account, setupError, ct);
        }

        // Начало периода: после отметки (или после прошлой успешной загрузки, если подтверждённых операций ещё нет)
        // с небольшим перекрытием; незавершённые операции перечитываем с самой ранней из них.
        var anchor = account.StatementWatermark ?? account.LastStatementSyncAt;
        var start = anchor.HasValue && anchor.Value - OverlapWindow > from ? anchor.Value - OverlapWindow : from;
        var earliestPending = await _db.BankStatementOperations.AsNoTracking()
            .Where(item => item.BankConnectionAccountId == account.Id && !item.IsBooked)
            .Select(item => (DateTime?)item.OperationDate)
            .MinAsync(ct);
        if (earliestPending.HasValue && earliestPending.Value < start)
        {
            start = earliestPending.Value > from ? earliestPending.Value : from;
        }

        int inserted = 0, updatedCount = 0;
        DateTime? lastBooked = null;
        DateTime? firstPending = null;
        try
        {
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var result = await provider.GetStatementPageAsync(credentials, account.ExternalAccountId, start, to, cursor, ct);
                var items = result.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.ExternalId) && item.ExternalId.Trim().Length <= 128)
                    .GroupBy(item => item.ExternalId.Trim(), StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .ToList();

                var ids = items.Select(item => item.ExternalId.Trim()).ToList();
                var known = await _db.BankStatementOperations
                    .Where(item => item.BankConnectionAccountId == account.Id && ids.Contains(item.ExternalOperationId))
                    .ToDictionaryAsync(item => item.ExternalOperationId, StringComparer.Ordinal, ct);

                foreach (var info in items)
                {
                    var key = info.ExternalId.Trim();
                    if (info.IsBooked)
                    {
                        lastBooked = lastBooked.HasValue && lastBooked > info.Date ? lastBooked : info.Date;
                    }
                    else
                    {
                        firstPending = firstPending.HasValue && firstPending < info.Date ? firstPending : info.Date;
                    }

                    if (known.TryGetValue(key, out var row))
                    {
                        // Подтверждённая операция больше не меняется; незавершённая обновляется.
                        if (!row.IsBooked)
                        {
                            Apply(row, info);
                            updatedCount++;
                        }

                        continue;
                    }

                    var created = new BankStatementOperation { BankConnectionAccountId = account.Id, ExternalOperationId = key };
                    Apply(created, info);
                    _db.BankStatementOperations.Add(created);
                    inserted++;
                }

                // Страница сохраняется сразу: память ограничена размером страницы, а сбой на поздней странице
                // не теряет уже полученное (повтор безопасен благодаря уникальному индексу).
                await _db.SaveChangesAsync(ct);

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
        catch (DbUpdateException ex)
        {
            // Параллельная загрузка того же счёта вставила те же операции: уникальный индекс защитил от дублей.
            _db.ChangeTracker.Clear();
            return new BankStatementSyncResult(inserted, updatedCount, account.StatementWatermark, Trim(ex.GetBaseException().Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAccountAsync(account, ex.Message, ct);
        }

        // Отметка сдвигается только после успешного сохранения всех страниц и не заходит за незавершённые операции.
        DateTime? newWatermark = lastBooked;
        if (newWatermark.HasValue && firstPending.HasValue && firstPending.Value <= newWatermark.Value)
        {
            newWatermark = firstPending.Value.AddTicks(-1);
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

    private async Task<(IBankProvider? Provider, BankCredentials? Credentials, string? Error)> PrepareAsync(
        BankConnection connection,
        CancellationToken ct)
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

        string access;
        string? refresh;
        try
        {
            access = _protector.Unprotect(connection.AccessTokenProtected);
            refresh = string.IsNullOrEmpty(connection.RefreshTokenProtected) ? null : _protector.Unprotect(connection.RefreshTokenProtected);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (null, null, "Токен доступа не удалось расшифровать: подключите банк заново.");
        }

        // Токен, срок которого вышел (или выходит через минуту), обновляем до обращения к банку.
        if (connection.AccessTokenExpiresAt.HasValue && connection.AccessTokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1))
        {
            if (refresh is null)
            {
                connection.Status = BankConnectionStatuses.ReauthRequired;
                connection.LastError = "Срок доступа истёк, а токен обновления не сохранён.";
                await _db.SaveChangesAsync(ct);
                return (null, null, connection.LastError);
            }

            try
            {
                var renewed = await provider.RefreshTokensAsync(refresh, ct);
                StoreTokens(connection, renewed.AccessToken, renewed.RefreshToken, renewed.ExpiresAt);
                // Новые токены сохраняем сразу: старый refresh-токен у многих банков после обмена недействителен.
                await _db.SaveChangesAsync(ct);
                access = renewed.AccessToken;
                refresh = renewed.RefreshToken ?? refresh;
            }
            catch (BankAuthorizationException ex)
            {
                connection.Status = BankConnectionStatuses.ReauthRequired;
                connection.LastError = Trim(ex.Message);
                await _db.SaveChangesAsync(ct);
                return (null, null, connection.LastError);
            }
        }

        return (provider, new BankCredentials(access, refresh), null);
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
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
