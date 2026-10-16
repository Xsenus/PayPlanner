using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;
using static PayPlanner.Api.Services.Banking.BankStatementImportRules;

namespace PayPlanner.Api.Services.Banking;

/// <summary>An explicit trusted caller; HTTP requests cannot choose a system actor.</summary>
public sealed class BankSyncInvocation
{
    internal ClaimsPrincipal? User { get; }
    internal string ExecutionId { get; }
    private BankSyncInvocation(ClaimsPrincipal? user, string executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId) || executionId.Length > 128) throw new ArgumentException("Укажите идентификатор запуска.", nameof(executionId));
        User = user;
        ExecutionId = executionId;
    }
    public static BankSyncInvocation ForUser(ClaimsPrincipal user, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return requestId == Guid.Empty ? throw new ArgumentException("Укажите идентификатор запроса.", nameof(requestId)) : new(user, requestId.ToString("N"));
    }
    public static BankSyncInvocation Scheduled(string executionId) => new(null, executionId);
}

internal sealed record BankStatementImportSession(
    BankStatementImportContext Context, int BatchId, int BankAccountId, string Provider, string Fingerprint, BankStatementSyncResult? Replay);

public sealed partial class BankStatementImportService
{
    internal async Task<BankStatementSyncResult?> ValidateNativePageAsync(BankStatementImportSession session, CancellationToken ct)
    {
        await LockAccountAsync(session.Context, ct);
        var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == session.BatchId, ct);
        if (batch.CompanyId != session.Context.CompanyId || batch.MoneyAccountId != session.Context.Account.Id
            || batch.BankConnectionAccountId != session.BankAccountId || batch.FileHash != session.Fingerprint
            || batch.Source != session.Context.Source || batch.ImportedByUserId != session.Context.ImportedByUserId)
            throw new UnauthorizedAccessException("Пакет больше не соответствует контексту загрузки.");
        return batch.CompletedAt.HasValue ? ReadNativeResult(batch.SourceResultJson) : null;
    }

    private static BankStatementSyncResult ReadNativeResult(string? value)
    {
        try
        {
            return JsonSerializer.Deserialize<BankStatementSyncResult>(value ?? string.Empty)
                ?? throw new InvalidOperationException("Сохранённый результат синхронизации отсутствует.");
        }
        catch (JsonException) { throw new InvalidOperationException("Сохранённый результат синхронизации требует проверки."); }
    }

    internal async Task<BankStatementImportSession> OpenNativeAsync(
        BankConnectionAccount source, DateTime from, DateTime to, BankSyncInvocation invocation, CancellationToken ct)
    {
        var companyId = source.BankConnection.LegalEntityId;
        if (!(await OwnCompanyRegistry.LoadIdsAsync(db, ct)).Contains(companyId))
            throw new UnauthorizedAccessException("Компания подключения не входит в реестр собственных компаний.");
        BankStatementImportAccessContext? access = null;
        if (invocation.User is not null)
        {
            access = await BankStatementImportAccess.ResolveAsync(db, invocation.User, ct);
            if (access is null || !access.Visibility.IsAdmin) throw new UnauthorizedAccessException("Банковская синхронизация доступна действующему администратору.");
        }
        if (string.IsNullOrWhiteSpace(source.AccountNumber) || string.IsNullOrWhiteSpace(source.Currency))
            throw new InvalidOperationException("Реквизиты банковского счёта требуют уточнения.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockCompanyAsync(companyId, ct);
        if (await db.BankConnectionAccounts.Where(account => account.Id == source.Id && account.BankConnection.LegalEntityId == companyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.ExternalAccountId, account => account.ExternalAccountId), ct) != 1)
            throw new UnauthorizedAccessException("Банковский счёт недоступен.");
        var moneyAccount = await db.MoneyAccounts.IgnoreQueryFilters().SingleOrDefaultAsync(account => account.BankConnectionAccountId == source.Id, ct);
        if (moneyAccount is null)
        {
            var physical = await db.MoneyAccounts.IgnoreQueryFilters()
                .Where(account => account.CompanyId == companyId && account.Source == MoneyOperationSource.Bank
                    && account.BankAccountNumber == source.AccountNumber && (account.BankBik ?? "") == (source.Bik ?? "") && account.Currency == source.Currency)
                .Take(2).ToListAsync(ct);
            if (physical.Count > 1 || physical.SingleOrDefault()?.BankConnectionAccountId is not null)
                throw new InvalidOperationException("Сопоставление банковского и денежного счёта требует подтверждения.");
            moneyAccount = physical.SingleOrDefault();
            if (moneyAccount is null)
            {
                moneyAccount = new MoneyAccount
                {
                    CompanyId = companyId,
                    Source = MoneyOperationSource.Bank,
                    Name = $"{source.BankConnection.Provider}: {source.AccountNumber} / {source.Currency} ({source.Bik})",
                    Currency = source.Currency,
                    BankAccountNumber = source.AccountNumber,
                    BankBik = source.Bik,
                    IsActive = BankAccountStatuses.AllowsNewDocuments(source.Status),
                };
                db.MoneyAccounts.Add(moneyAccount);
            }
            moneyAccount.BankConnectionAccountId = source.Id;
            await db.SaveChangesAsync(ct);
        }
        if (moneyAccount.CompanyId != companyId || moneyAccount.Source != MoneyOperationSource.Bank
            || moneyAccount.BankAccountNumber != source.AccountNumber || (moneyAccount.BankBik ?? "") != (source.Bik ?? "") || moneyAccount.Currency != source.Currency)
            throw new InvalidOperationException("Денежный счёт не соответствует банковской привязке.");
        var context = new BankStatementImportContext(access, invocation.User, moneyAccount, companyId,
            access?.Visibility.UserId, access?.Visibility.UserId,
            invocation.User is null ? BankStatementImportSources.BackgroundBank : BankStatementImportSources.ManualBank,
            invocation.User is null ? BankStatementImportTriggers.Background : BankStatementImportTriggers.Manual, IsNativeInvocation: true);
        context = await LockAccountAsync(context, ct);
        var key = Hash($"bank-package|{companyId}|{moneyAccount.Id}|{context.Source}|{context.ImportedByUserId}|{invocation.ExecutionId}");
        var fingerprint = Hash($"bank-period|{source.Id}|{AsUtc(from):O}|{AsUtc(to):O}");
        var batch = await db.BankStatementImportBatches.AsNoTracking().SingleOrDefaultAsync(item => item.PackageKey == key, ct);
        BankStatementSyncResult? replay = null;
        if (batch is not null)
        {
            if (batch.FileHash != fingerprint || batch.CompanyId != companyId || batch.MoneyAccountId != moneyAccount.Id
                || batch.Source != context.Source || batch.ImportedByUserId != context.ImportedByUserId)
                throw new InvalidOperationException("Идентификатор запуска уже использован с другим контекстом или периодом.");
            if (batch.CompletedAt.HasValue)
            {
                replay = ReadNativeResult(batch.SourceResultJson);
            }
        }
        else
        {
            batch = new BankStatementImportBatch
            {
                CompanyId = companyId,
                MoneyAccountId = moneyAccount.Id,
                BankConnectionAccountId = source.Id,
                ImportedByUserId = context.ImportedByUserId,
                ResponsibleUserId = context.ResponsibleUserId,
                Source = context.Source,
                Trigger = context.Trigger,
                PackageKey = key,
                FileHash = fingerprint,
                FileName = $"Банк: {source.BankConnection.Provider}",
                PeriodFrom = AsUtc(from),
                PeriodTo = AsUtc(to),
                Status = BankStatementImportBatchStatus.Loading,
            };
            db.BankStatementImportBatches.Add(batch);
            await db.SaveChangesAsync(ct);
            db.Entry(batch).State = EntityState.Detached;
        }
        await transaction.CommitAsync(ct);
        return new(context, batch.Id, source.Id, source.BankConnection.Provider, fingerprint, replay);
    }

    internal async Task ImportNativePageAsync(BankStatementImportSession session, IReadOnlyCollection<int> rawIds, CancellationToken ct)
    {
        var ids = rawIds.Distinct().ToArray();
        if (ids.Length > ChunkSize) throw new InvalidOperationException("Банковская страница превышает допустимый размер обработки.");
        var raw = await db.BankStatementOperations.AsNoTracking().Where(operation => operation.BankConnectionAccountId == session.BankAccountId && ids.Contains(operation.Id))
            .OrderBy(operation => operation.Id).ToListAsync(ct);
        if (raw.Count != ids.Length) throw new UnauthorizedAccessException("Принадлежность банковских строк не подтверждена.");
        var items = raw.Select(operation => new BankStatementImportItem(
            operation.Id, operation.OperationDate, operation.IsIncome ? MoneyOperationDirection.Income : MoneyOperationDirection.Expense,
            operation.Amount, session.Context.Account.Name, operation.CounterpartyName, operation.Purpose, null)
        {
            BankStatementOperationId = operation.Id,
            ExternalOperationId = operation.ExternalOperationId,
            IsBooked = operation.IsBooked,
            MovementKey = Hash($"native|{session.Context.CompanyId}|{session.Context.Account.Id}|{session.Provider}|{operation.ExternalOperationId}"),
        }).ToArray();
        await ImportAsync(session.Context, items, new BankStatementImportResultDto { RowsRead = items.Length },
            $"Банк: {session.Provider}", 0, session.Fingerprint, Array.Empty<BankStatementCategoryRule>(), ct, appendBatchId: session.BatchId);
    }

    internal async Task CompleteNativeAsync(BankStatementImportSession session, BankStatementSyncResult? result, CancellationToken ct)
    {
        // Join the caller's account transaction so watermark, journal and receipt commit together.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockAccountAsync(session.Context, ct);
        var tracked = db.BankStatementImportBatches.Local.FirstOrDefault(item => item.Id == session.BatchId);
        if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
        var batch = await db.BankStatementImportBatches.SingleAsync(item => item.Id == session.BatchId, ct);
        if (batch.CompletedAt.HasValue) return;
        if (result is null)
        {
            batch.Status = BankStatementImportBatchStatus.AppliedWithErrors;
            batch.ErrorSummary = "Получение выписки не завершено; сохранённые страницы доступны в истории.";
        }
        else
        {
            batch.Status = batch.ErrorRows == 0 ? BankStatementImportBatchStatus.Applied : BankStatementImportBatchStatus.AppliedWithErrors;
            batch.CompletedAt = DateTime.UtcNow;
            batch.SourceResultJson = JsonSerializer.Serialize(result);
            if (batch.ErrorRows == 0) batch.ErrorSummary = null;
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        db.Entry(batch).State = EntityState.Detached;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
