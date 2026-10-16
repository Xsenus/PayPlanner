using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;
using static PayPlanner.Api.Services.Banking.BankStatementImportRules;

namespace PayPlanner.Api.Services.Banking;

internal sealed record BankStatementImportContext(
    BankStatementImportAccessContext? Access,
    ClaimsPrincipal? User,
    MoneyAccount Account,
    int CompanyId,
    int? ImportedByUserId,
    int? ResponsibleUserId,
    string Source,
    string Trigger,
    bool IsNativeInvocation = false);

internal sealed record BankStatementImportPreparation(BankStatementImportContext? Context, int Status, string? Error = null);

/// <summary>Persists one attributed package and its canonical money identities, independently of its transport.</summary>
public sealed partial class BankStatementImportService(PaymentContext db)
{
    private const int ChunkSize = 500;
    private const int MaxResultOperations = 100;

    internal async Task<BankStatementImportPreparation> PrepareFileAsync(
        ClaimsPrincipal user, int? companyId, int? moneyAccountId, string trigger, CancellationToken ct)
    {
        var access = await BankStatementImportAccess.ResolveAsync(db, user, ct);
        if (access is null || !access.Permission.CanView || !access.Permission.CanCreate) return new(null, 403);
        if (!companyId.HasValue || companyId <= 0) return new(null, 400, "Выберите собственную компанию для выписки.");
        if (!access.OwnCompanyIds.Contains(companyId.Value)) return new(null, 400, "Компания не входит в реестр собственных компаний.");
        if (!access.AllowedCompanyIds.Contains(companyId.Value)) return new(null, 403);
        if (!moneyAccountId.HasValue || moneyAccountId <= 0) return new(null, 400, "Выберите банковский счёт компании.");
        if (!await db.LegalEntities.AnyAsync(company => company.Id == companyId.Value, ct))
            return new(null, 404, "Профиль собственной компании недоступен.");
        var account = await BankStatementImportAccess.ScopeAccounts(db.MoneyAccounts, access)
            .AsNoTracking().FirstOrDefaultAsync(item => item.Id == moneyAccountId && item.CompanyId == companyId
                && item.Source == MoneyOperationSource.Bank && item.IsActive, ct);
        if (account is null) return new(null, 404);
        if (account.BankConnectionAccountId.HasValue && !await db.BankConnectionAccounts.AnyAsync(bank =>
                bank.Id == account.BankConnectionAccountId.Value && bank.BankConnection.LegalEntityId == companyId
                && bank.AccountNumber == account.BankAccountNumber && (bank.Bik ?? "") == (account.BankBik ?? "")
                && bank.Currency == account.Currency, ct))
            return new(null, 400, "Банковская привязка счёта требует уточнения.");
        return new(new(access, user, account, companyId.Value, access.Visibility.UserId, access.Visibility.UserId,
            BankStatementImportSources.File, trigger), 200);
    }

    internal static BankStatementImportContext FromAuthorizedBatch(
        BankStatementImportAccessContext access, ClaimsPrincipal user, MoneyAccount account, BankStatementImportBatch batch)
        => new(access, user, account, batch.CompanyId!.Value, access.Visibility.UserId,
            batch.ResponsibleUserId, batch.Source!, BankStatementImportTriggers.Manual);

    internal static string PackageKey(BankStatementImportContext context, string fileHash, int? parentId = null, DateTime? rollbackAt = null)
        => Hash($"package|{context.CompanyId}|{context.Account.Id}|{fileHash}|{parentId}|{rollbackAt?.Ticks}");

    internal async Task<BankStatementImportResultDto> ImportAsync(
        BankStatementImportContext context, IReadOnlyCollection<BankStatementImportItem> items,
        BankStatementImportResultDto parsed, string fileName, long fileSize, string fileHash,
        IReadOnlyCollection<BankStatementCategoryRule> manualRules, CancellationToken ct,
        int? parentId = null, DateTime? rollbackAt = null, int? appendBatchId = null)
    {
        var packageKey = PackageKey(context, fileHash, parentId, rollbackAt);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        context = await LockAccountAsync(context, ct);
        if (parentId.HasValue && (context.Access is null || !await BankStatementImportAccess.ScopeBatches(db,
                db.BankStatementImportBatches, context.Access, forMutation: true).AnyAsync(batch => batch.Id == parentId, ct)))
            throw new UnauthorizedAccessException("Исходный пакет изменён или недоступен.");
        if (!appendBatchId.HasValue)
        {
            var previous = await FindReplayAsync(context, packageKey, ct);
            if (previous is not null) return previous;
        }
        var result = new BankStatementImportResultDto { RowsRead = parsed.RowsRead, Skipped = parsed.Skipped };
        result.Errors.AddRange(parsed.Errors.Take(50));
        var batch = appendBatchId.HasValue
            ? await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == appendBatchId, ct)
            : new BankStatementImportBatch
            {
                CompanyId = context.CompanyId,
                MoneyAccountId = context.Account.Id,
                BankConnectionAccountId = context.Account.BankConnectionAccountId,
                ImportedByUserId = context.ImportedByUserId,
                ResponsibleUserId = context.ResponsibleUserId,
                Source = context.Source,
                Trigger = context.Trigger,
                ParentBatchId = parentId,
                PackageKey = packageKey,
                FileName = Path.GetFileName(fileName),
                FileSize = fileSize,
                FileHash = fileHash,
                RowsRead = parsed.RowsRead,
                PeriodFrom = items.Count == 0 ? null : items.Min(item => item.Date),
                PeriodTo = items.Count == 0 ? null : items.Max(item => item.Date),
            };
        if (appendBatchId.HasValue)
        {
            if (batch.CompanyId != context.CompanyId || batch.MoneyAccountId != context.Account.Id
                || batch.Source != context.Source || batch.ImportedByUserId != context.ImportedByUserId)
                throw new UnauthorizedAccessException("Пакет не соответствует источнику банковской страницы.");
            // A competing request may have completed between raw-page commit and this account lease.
            if (batch.CompletedAt.HasValue)
                return new BankStatementImportResultDto { BatchId = batch.Id, RowsRead = batch.RowsRead, Created = batch.CreatedRows, Skipped = batch.SkippedRows };
        }
        else
        {
            db.BankStatementImportBatches.Add(batch);
            await db.SaveChangesAsync(ct);
            db.Entry(batch).State = EntityState.Detached;
        }
        var newRows = 0;
        var createdDelta = 0;
        var errorsDelta = 0;
        var incomeTypes = await db.IncomeTypes.AsNoTracking().Where(item => item.IsActive)
            .Select(item => new BankStatementIncomeTypeCandidate(item.Id, item.Name, item.PaymentType)).ToListAsync(ct);
        var seenFacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in items.Chunk(ChunkSize))
        {
            var keys = chunk.Select(item => ResolveMovementKey(context, fileHash, item)).ToArray();
            var existing = await db.MoneyOperations.IgnoreQueryFilters()
                .Where(item => item.SourceMovementKey != null && keys.Contains(item.SourceMovementKey))
                .ToDictionaryAsync(item => item.SourceMovementKey!, StringComparer.Ordinal, ct);
            var rowKeys = chunk.Select(item => ResolveRowKey(fileHash, item)).ToArray();
            var previousRows = appendBatchId.HasValue
                ? await db.BankStatementImportRows.Where(row => row.BatchId == batch.Id && row.RowIdentityKey != null && rowKeys.Contains(row.RowIdentityKey))
                    .ToDictionaryAsync(row => row.RowIdentityKey!, StringComparer.Ordinal, ct)
                : new Dictionary<string, BankStatementImportRow>(StringComparer.Ordinal);
            var oldStates = previousRows.ToDictionary(pair => pair.Key, pair => pair.Value.Status, StringComparer.Ordinal);
            var ambiguousFacts = await FindAmbiguousFactsAsync(context, chunk.Where(item => !item.BankStatementOperationId.HasValue).ToArray(), ct);
            var candidates = await LoadInvoiceCandidatesAsync(context, chunk, ct);
            var reserved = new Dictionary<int, decimal>();
            var created = new List<MoneyOperation>();
            foreach (var item in chunk)
            {
                ct.ThrowIfCancellationRequested();
                var key = ResolveMovementKey(context, fileHash, item);
                var rowKey = ResolveRowKey(fileHash, item);
                var row = previousRows.GetValueOrDefault(rowKey);
                var isNewRow = row is null;
                if (row is { Status: BankStatementImportRowStatus.Created, MoneyOperationId: not null })
                {
                    if (!existing.TryGetValue(key, out var preserved) || preserved.Id != row.MoneyOperationId)
                        throw new InvalidOperationException("Исходная связь проведённой строки требует проверки.");
                    EnsureCanonicalAccess(context, preserved);
                    result.Skipped++;
                    continue;
                }
                if (row is null)
                {
                    row = CreateImportRow(batch, item, key, BankStatementImportRowStatus.Created, null);
                    row.RowIdentityKey = rowKey;
                    previousRows.Add(rowKey, row);
                    newRows++;
                }
                row.Status = BankStatementImportRowStatus.Created;
                row.Message = null;
                row.Date = item.Date;
                row.Direction = item.Direction;
                row.Amount = item.Amount;
                row.CounterpartyName = item.CounterpartyName;
                row.Purpose = item.Purpose;
                row.PaymentOrderNumber = item.DocumentNumber;
                // Link by persisted ID so a tracked parent collection does not
                // retain every detached row or duplicate it on the next read.
                row.Batch = null;
                row.BatchId = batch.Id;
                if (isNewRow) db.BankStatementImportRows.Add(row);
                row.MovementKey = key;
                row.OriginRowId = item.OriginRowId;
                row.BankStatementOperationId = item.BankStatementOperationId;
                row.ExternalOperationId = item.ExternalOperationId;
                row.Currency = context.Account.Currency;
                row.IsBooked = item.IsBooked;
                if (!item.IsBooked)
                {
                    row.Status = BankStatementImportRowStatus.Pending;
                    row.Message = "Операция ещё не проведена банком.";
                    result.Skipped++;
                    continue;
                }
                if (item.Amount <= 0 || item.Date == default)
                {
                    row.Status = BankStatementImportRowStatus.Error;
                    row.Message = "Уточните дату и положительную сумму операции.";
                    result.Skipped++;
                    if (result.Errors.Count < 50) result.Errors.Add($"Строка {item.RowNumber}: {row.Message}");
                    continue;
                }
                if (existing.TryGetValue(key, out var canonical))
                {
                    EnsureCanonicalAccess(context, canonical);
                    if (canonical.Amount != item.Amount || canonical.Date.Date != item.Date.Date
                        || canonical.Direction != item.Direction || !string.Equals(canonical.Currency, context.Account.Currency, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Status = BankStatementImportRowStatus.ClarificationRequired;
                        row.Message = "Данные источника расходятся с ранее учтённой операцией.";
                        result.Skipped++;
                        continue;
                    }
                    row.MoneyOperationId = canonical.Id;
                    if (!db.Entry(canonical).Property<bool>("IsDeleted").CurrentValue)
                    {
                        row.Status = BankStatementImportRowStatus.Skipped;
                        row.Message = "Операция уже учтена; сохранена связь с исходной записью.";
                        result.Skipped++;
                        continue;
                    }
                    if (!parentId.HasValue)
                    {
                        row.Status = BankStatementImportRowStatus.ClarificationRequired;
                        row.Message = "Исходная операция в архиве; восстановление выполняется через повторную обработку.";
                        result.Skipped++;
                        continue;
                    }
                    // Preserve the occupied identity and audit history after an explicit retry.
                    db.Entry(canonical).Property("IsDeleted").CurrentValue = false;
                    db.Entry(canonical).Property("DeletedAt").CurrentValue = null;
                    db.Entry(canonical).Property("DeletedByUserId").CurrentValue = null;
                    db.Entry(canonical).Property("DeletedByUserName").CurrentValue = null;
                    row.MoneyOperation = canonical;
                    created.Add(canonical);
                    result.Created++;
                    continue;
                }
                var fact = BuildBankStatementImportKey(item.Date, item.Direction, item.Amount, item.DocumentNumber, item.CounterpartyName);
                if (!item.BankStatementOperationId.HasValue && (!seenFacts.Add(fact) || ambiguousFacts.Contains(fact)))
                {
                    row.Status = BankStatementImportRowStatus.ClarificationRequired;
                    row.Message = "Совпадающие реквизиты требуют уточнения; новая денежная запись не создана.";
                    result.Skipped++;
                    continue;
                }
                var incomeType = ResolveBankStatementIncomeType(item, incomeTypes, manualRules);
                var operation = new MoneyOperation
                {
                    Source = MoneyOperationSource.Bank,
                    AccountingType = MoneyOperationAccountingType.Fact,
                    Direction = item.Direction,
                    MoneyAccountId = context.Account.Id,
                    AccountName = context.Account.Name,
                    ResponsibleUserId = context.ResponsibleUserId,
                    SourceMovementKey = key,
                    BankStatementImportBatchId = batch.Id,
                    CounterpartyName = item.CounterpartyName,
                    IncomeTypeId = incomeType?.Id,
                    CategoryName = incomeType is null ? ResolveBankStatementCategoryName(item, manualRules) : null,
                    Amount = item.Amount,
                    Currency = context.Account.Currency,
                    Date = item.Date,
                    RecognitionDate = item.Date,
                    PaymentOrderNumber = item.DocumentNumber,
                    Purpose = item.Purpose,
                    MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
                };
                var match = string.Equals(operation.Currency, "RUB", StringComparison.OrdinalIgnoreCase)
                    ? TryResolveBankStatementInvoiceMatch(item, candidates, reserved) : null;
                if (match is not null)
                {
                    reserved[match.InvoiceId] = reserved.GetValueOrDefault(match.InvoiceId) + item.Amount;
                    operation.LinkedInvoiceId = match.InvoiceId;
                    operation.PaidInvoiceNumber = match.InvoiceNumber;
                    operation.PaidAccountName = match.InvoiceNumber;
                    operation.MatchStatus = MoneyOperationMatchStatus.Matched;
                    operation.MatchedAt = DateTime.UtcNow;
                    operation.MatchComment = BuildBankStatementAutoMatchComment(item, match);
                    row.Message = operation.MatchComment;
                }
                ApplyAdvanceReviewHint(operation);
                db.MoneyOperations.Add(operation);
                row.MoneyOperation = operation;
                existing.Add(key, operation);
                created.Add(operation);
                result.Created++;
            }
            await db.SaveChangesAsync(ct);
            foreach (var pair in previousRows)
            {
                var old = oldStates.GetValueOrDefault(pair.Key);
                createdDelta += (pair.Value.Status == BankStatementImportRowStatus.Created ? 1 : 0)
                    - (old == BankStatementImportRowStatus.Created ? 1 : 0);
                errorsDelta += (pair.Value.Status == BankStatementImportRowStatus.Error ? 1 : 0)
                    - (old == BankStatementImportRowStatus.Error ? 1 : 0);
            }
            foreach (var operation in created.Where(item => item.LinkedInvoiceId.HasValue && item.MatchStatus == MoneyOperationMatchStatus.Matched))
                await MoneyOperationInvoiceAllocationService.ApplyAsync(db, operation, operation.LinkedInvoiceId!.Value, ct);
            await db.SaveChangesAsync(ct);
            foreach (var operation in created.Take(Math.Max(0, MaxResultOperations - result.Operations.Count)))
                result.Operations.Add(new BankStatementImportCreatedOperationDto
                {
                    Id = operation.Id,
                    Date = operation.Date,
                    Direction = operation.Direction,
                    Amount = operation.Amount,
                    CounterpartyName = operation.CounterpartyName,
                    PaymentOrderNumber = operation.PaymentOrderNumber,
                    MatchStatus = operation.MatchStatus,
                });
            foreach (var row in db.ChangeTracker.Entries<BankStatementImportRow>().Where(entry => entry.Entity.BatchId == batch.Id).ToArray()) row.State = EntityState.Detached;
            foreach (var operation in existing.Values) db.Entry(operation).State = EntityState.Detached;
        }
        var trackedBatch = db.BankStatementImportBatches.Local.FirstOrDefault(item => item.Id == batch.Id);
        if (trackedBatch is not null) db.Entry(trackedBatch).State = EntityState.Detached;
        db.Attach(batch);
        batch.RowsRead = appendBatchId.HasValue ? batch.RowsRead + newRows : parsed.RowsRead;
        batch.CreatedRows = appendBatchId.HasValue ? batch.CreatedRows + createdDelta : result.Created;
        batch.SkippedRows = appendBatchId.HasValue ? batch.SkippedRows + newRows - createdDelta : result.Skipped;
        batch.ErrorRows = appendBatchId.HasValue ? batch.ErrorRows + errorsDelta : parsed.Errors.Count + errorsDelta;
        batch.ErrorSummary = result.Errors.Count == 0 ? null : string.Join(Environment.NewLine, result.Errors);
        batch.Status = appendBatchId.HasValue ? BankStatementImportBatchStatus.Loading
            : batch.ErrorRows == 0 ? BankStatementImportBatchStatus.Applied : BankStatementImportBatchStatus.AppliedWithErrors;
        if (!appendBatchId.HasValue) batch.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        result.BatchId = batch.Id;
        return result;
    }

    private async Task<BankStatementImportResultDto?> FindReplayAsync(BankStatementImportContext context, string packageKey, CancellationToken ct)
    {
        var existing = await db.BankStatementImportBatches.AsNoTracking().FirstOrDefaultAsync(batch => batch.PackageKey == packageKey, ct);
        if (existing is null) return null;
        if (context.Access is null || !await BankStatementImportAccess.ScopeBatches(db, db.BankStatementImportBatches, context.Access, forMutation: true)
                .AnyAsync(batch => batch.Id == existing.Id && batch.CompanyId == context.CompanyId && batch.MoneyAccountId == context.Account.Id, ct))
            throw new UnauthorizedAccessException("Результат загрузки недоступен в текущем контексте.");
        var result = new BankStatementImportResultDto { BatchId = existing.Id, RowsRead = existing.RowsRead, Created = existing.CreatedRows, Skipped = existing.SkippedRows };
        if (!string.IsNullOrWhiteSpace(existing.ErrorSummary)) result.Errors.AddRange(existing.ErrorSummary.Split(Environment.NewLine).Take(50));
        result.Operations = await db.BankStatementImportRows.IgnoreQueryFilters().AsNoTracking().Where(row => row.BatchId == existing.Id && row.Status == BankStatementImportRowStatus.Created && row.MoneyOperationId.HasValue)
            .OrderBy(row => row.Id).Take(MaxResultOperations).Select(row => new BankStatementImportCreatedOperationDto
            {
                Id = row.MoneyOperationId!.Value,
                Date = row.Date,
                Amount = row.Amount,
                Direction = row.Direction,
                CounterpartyName = row.CounterpartyName,
                PaymentOrderNumber = row.PaymentOrderNumber,
                MatchStatus = row.MoneyOperation!.MatchStatus,
            }).ToListAsync(ct);
        return result;
    }

    private async Task<List<BankStatementInvoiceCandidate>> LoadInvoiceCandidatesAsync(
        BankStatementImportContext context, IReadOnlyCollection<BankStatementImportItem> items, CancellationToken ct)
    {
        if (context.Access is null || context.User is null || !context.Access.Permission.CanEdit) return [];
        var accounts = await db.ResolveSectionAccessAsync(context.User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (accounts?.Permission is not { CanView: true, CanEdit: true }) return [];
        var tokens = items.SelectMany(item => ExtractInvoiceNumberTokens(item.Purpose)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var types = items.Select(item => item.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income).Distinct().ToArray();
        var candidates = await InvoicePaymentAccess.ScopeHeaders(db, accounts, context.CompanyId)
            .Where(payment => payment.SellerCompanyId == context.CompanyId && payment.DocumentKind == DocumentKind.Invoice
                && types.Contains(payment.Type) && payment.Status != PaymentStatus.Cancelled && payment.InvoiceId.HasValue)
            .Include(payment => payment.Invoice).Include(payment => payment.Client).Include(payment => payment.ExpenseClient)
            .Include(payment => payment.ExpenseLegalEntity).Include(payment => payment.Contract).ToListAsync(ct);
        return candidates.Select(payment => new BankStatementInvoiceCandidate(
            payment.Id, payment.InvoiceId!.Value, payment.Invoice?.Number ?? payment.Account ?? string.Empty, payment.Account,
            payment.Amount, payment.PaidAmount, payment.Status, payment.Type,
            payment.Type == PaymentType.Expense ? payment.ExpenseClientId ?? payment.ClientId : payment.ClientId,
            ResolveInvoiceCandidateCounterpartyName(payment), payment.CounterpartyName,
            BuildInvoiceNumberTokenSet(payment.Invoice?.Number, payment.Account, payment.LegacyAccountNumber), BuildInvoiceNumberTokenSet(payment.Contract?.Number)))
            .Where(candidate => tokens.Count == 0 || candidate.MatchingNumberTokens.Any(tokens.Contains)
                || items.Any(item => item.Direction == MoneyOperationDirection.Income
                    && Math.Abs(candidate.OutstandingAmount - item.Amount) <= 0.009m && NamesLookRelated(item.CounterpartyName, candidate.ClientName ?? candidate.CounterpartyName))).ToList();
    }

    private async Task<HashSet<string>> FindAmbiguousFactsAsync(
        BankStatementImportContext context, IReadOnlyCollection<BankStatementImportItem> items, CancellationToken ct)
    {
        var requested = items.Where(item => item.Amount > 0 && item.Date != default)
            .Select(item => BuildBankStatementImportKey(item.Date, item.Direction, item.Amount, item.DocumentNumber, item.CounterpartyName))
            .ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (requested.Count == 0) return found;
        if (context.Access is null) return found;
        var from = items.Where(item => item.Date != default).Min(item => item.Date).Date;
        var to = items.Where(item => item.Date != default).Max(item => item.Date).Date.AddDays(1);
        var amounts = items.Select(item => item.Amount).Distinct().ToArray();
        var minAmount = amounts.Min();
        var maxAmount = amounts.Max();
        var names = items.Select(item => item.CounterpartyName ?? string.Empty).Distinct().ToArray();
        var documents = items.Select(item => item.DocumentNumber ?? string.Empty).Distinct().ToArray();
        var visible = context.Access.Visibility.VisibleUserIds.ToArray();
        var all = context.Access.Visibility.SeesAll;
        var query = db.MoneyOperations.IgnoreQueryFilters().AsNoTracking()
            .Where(operation => operation.MoneyAccountId == context.Account.Id && operation.Source == MoneyOperationSource.Bank
                && operation.AccountingType == MoneyOperationAccountingType.Fact && operation.Currency == context.Account.Currency
                && operation.Date >= from && operation.Date < to && operation.Amount >= minAmount && operation.Amount <= maxAmount
                && names.Contains(operation.CounterpartyName ?? string.Empty)
                && documents.Contains(operation.PaymentOrderNumber ?? string.Empty)
                && (all || operation.ResponsibleUserId.HasValue && visible.Contains(operation.ResponsibleUserId.Value)))
            .Select(operation => new { operation.Date, operation.Direction, operation.Amount, operation.PaymentOrderNumber, operation.CounterpartyName });
        await foreach (var fact in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            var key = BuildBankStatementImportKey(fact.Date, fact.Direction, fact.Amount, fact.PaymentOrderNumber, fact.CounterpartyName);
            if (requested.Contains(key)) found.Add(key);
            if (found.Count == requested.Count) break;
        }
        if (found.Count == requested.Count || !string.Equals(context.Account.Currency, "RUB", StringComparison.OrdinalIgnoreCase)) return found;
        // Legacy manual payments have no reliable movement identity. They may
        // require reconciliation, but similar values never prove a duplicate.
        var legacy = db.Payments.AsNoTracking().ApplyPaymentVisibility(context.Access.Visibility)
            .Where(payment => payment.SellerCompanyId == context.CompanyId && payment.Status == PaymentStatus.Completed
                && payment.DocumentKind != DocumentKind.Invoice
                && (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) >= from
                && (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) < to
                && (payment.PaidAmount > 0m ? payment.PaidAmount : payment.Amount) >= minAmount
                && (payment.PaidAmount > 0m ? payment.PaidAmount : payment.Amount) <= maxAmount
                && names.Contains(payment.CounterpartyName ?? string.Empty)
                && documents.Contains(payment.PaymentOrderNumber ?? string.Empty))
            .Select(payment => new
            {
                Date = payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date,
                payment.Type,
                Amount = payment.PaidAmount > 0m ? payment.PaidAmount : payment.Amount,
                payment.PaymentOrderNumber,
                payment.CounterpartyName,
                SourceName = payment.PaymentSource == null ? null : payment.PaymentSource.Name,
            });
        await foreach (var fact in legacy.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!IsBankPaymentSource(fact.SourceName)) continue;
            var key = BuildBankStatementImportKey(fact.Date, fact.Type == PaymentType.Expense ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income,
                fact.Amount, fact.PaymentOrderNumber, fact.CounterpartyName);
            if (requested.Contains(key)) found.Add(key);
            if (found.Count == requested.Count) break;
        }
        return found;
    }

    private static void EnsureCanonicalAccess(BankStatementImportContext context, MoneyOperation operation)
    {
        if (operation.Source != MoneyOperationSource.Bank || operation.MoneyAccountId != context.Account.Id
            || context.Access is not null && !context.Access.Visibility.SeesAll && (!operation.ResponsibleUserId.HasValue || !context.Access.Visibility.VisibleUserIds.Contains(operation.ResponsibleUserId.Value)))
            throw new UnauthorizedAccessException("Принадлежность исходной операции не подтверждена.");
    }

    private static string ResolveMovementKey(BankStatementImportContext context, string fileHash, BankStatementImportItem item)
        => item.MovementKey ?? Hash($"movement|{context.CompanyId}|{context.Account.Id}|{fileHash}|{item.RowNumber}");

    private static string ResolveRowKey(string fileHash, BankStatementImportItem item)
        => item.BankStatementOperationId.HasValue ? Hash($"raw-row|{item.BankStatementOperationId.Value}")
            : Hash($"file-row|{fileHash}|{item.RowNumber}");

    private async Task LockCompanyAsync(int companyId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Проверка компании требует транзакции загрузки.");
        if (await db.LegalEntities.Where(company => company.Id == companyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ShortName, company => company.ShortName), ct) != 1)
            throw new UnauthorizedAccessException("Профиль собственной компании удалён или недоступен.");
    }

    private async Task<BankStatementImportContext> LockAccountAsync(BankStatementImportContext context, CancellationToken ct)
    {
        if (context.Access is null && context.Source != BankStatementImportSources.BackgroundBank)
            throw new UnauthorizedAccessException("Источник запуска не подтверждён.");
        // Match manual operations and account CRUD: company -> bank -> money.
        // Keep the lease until the caller commits, before raw or financial writes.
        await LockCompanyAsync(context.CompanyId, ct);
        if (context.IsNativeInvocation && !context.Account.BankConnectionAccountId.HasValue)
            throw new UnauthorizedAccessException("Банковская привязка счёта не подтверждена.");
        if (context.Account.BankConnectionAccountId.HasValue && await db.BankConnectionAccounts.Where(bank =>
                bank.Id == context.Account.BankConnectionAccountId && bank.BankConnection.LegalEntityId == context.CompanyId
                && bank.AccountNumber == context.Account.BankAccountNumber && (bank.Bik ?? "") == (context.Account.BankBik ?? "")
                && bank.Currency == context.Account.Currency
                && (!context.IsNativeInvocation || bank.StatementsEnabled
                    && bank.Status != BankAccountStatuses.Unavailable && bank.Status != BankAccountStatuses.NotFoundInBank))
            .ExecuteUpdateAsync(setters => setters.SetProperty(bank => bank.ExternalAccountId, bank => bank.ExternalAccountId), ct) != 1)
            throw new UnauthorizedAccessException("Банковская привязка счёта изменена или недоступна.");
        if (await db.MoneyAccounts.Where(account => account.Id == context.Account.Id && account.CompanyId == context.CompanyId
                && account.Source == MoneyOperationSource.Bank && account.BankConnectionAccountId == context.Account.BankConnectionAccountId
                && account.BankAccountNumber == context.Account.BankAccountNumber && account.BankBik == context.Account.BankBik
                && account.Currency == context.Account.Currency && account.IsActive == context.Account.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Name, account => account.Name), ct) != 1)
            throw new UnauthorizedAccessException("Счёт изменён или недоступен.");
        // Read permissions after the account lease, including requests that waited for another transaction.
        if (context.User is not null)
        {
            var fresh = await BankStatementImportAccess.ResolveAsync(db, context.User, ct);
            if (fresh is null || !fresh.Permission.CanView || !fresh.Permission.CanCreate || !fresh.AllowedCompanyIds.Contains(context.CompanyId)
                || context.IsNativeInvocation && !fresh.Visibility.IsAdmin)
                throw new UnauthorizedAccessException("Доступ к загрузке изменён.");
            context = context with { Access = fresh };
        }
        else if (!(await PayPlanner.Api.Services.Companies.OwnCompanyRegistry.LoadIdsAsync(db, ct)).Contains(context.CompanyId))
            throw new UnauthorizedAccessException("Компания больше не входит в реестр собственных.");
        return context;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
