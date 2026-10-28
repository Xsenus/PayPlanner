using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Banking;
using static PayPlanner.Api.Services.Banking.BankStatementImportRules;

namespace PayPlanner.Api.Services;

// Inputs are already restricted by the caller's user or explicit system scope.
// This query layer neither grants access nor constructs a user principal.
internal sealed record MoneyJournalQuerySources(
    IQueryable<MoneyOperation> CustomOperations,
    IQueryable<Payment> PaymentOperations,
    IQueryable<MoneyOperation> ReadableInvoiceOperations,
    bool PreserveInheritedVisibility,
    IQueryable<Contract>? ReadableContracts = null,
    IQueryable<Act>? ReadableActs = null);

internal sealed class MoneyJournalQuery(PaymentContext db, MoneyJournalQuerySources sources)
{
    private readonly PaymentContext _db = db;
    private readonly MoneyJournalQuerySources _sources = sources;

    public static IQueryable<MoneyOperationQueryRow> ApplyVisibility(
        IQueryable<MoneyOperationQueryRow> query,
        DataVisibilityAccess? visibility)
    {
        if (visibility is null)
        {
            return query.Where(_ => false);
        }

        if (visibility.SeesAll)
        {
            return query;
        }

        var visibleUserIds = visibility.VisibleUserIds.ToArray();
        return query.Where(item =>
            (item.VisibilityResponsibleUserId ?? item.ResponsibleUserId).HasValue
            && visibleUserIds.Contains((item.VisibilityResponsibleUserId ?? item.ResponsibleUserId).GetValueOrDefault()));
    }

    public Task<List<MoneyOperationDailyBalanceDto>> BuildDailyBalanceRowsAsync(
        DateTime? from, DateTime? to, MoneyOperationSource? source, string? accountName,
        DataVisibilityAccess visibility, CancellationToken cancellationToken)
        => BuildDailyBalanceRowsAsync(from, to, source, accountName, MoneyJournalVisibility.FromUser(visibility), cancellationToken);

    public async Task<List<MoneyOperationDailyBalanceDto>> BuildDailyBalanceRowsAsync(
        DateTime? from, DateTime? to, MoneyOperationSource? source, string? accountName,
        MoneyJournalVisibility visibility, CancellationToken cancellationToken, MoneyReconciliationScope? scope = null, int? moneyAccountId = null)
    {
        const int maxBalanceRows = 25_000;
        IQueryable<MoneyOperationQueryRow> Rows(DateTime? start, DateTime? end)
        {
            var query = Build(start, end, source, null, MoneyOperationAccountingType.Fact, moneyAccountId, accountName, null, null);
            query = visibility.SeesAll ? query : query.Where(item =>
                (item.VisibilityResponsibleUserId ?? item.ResponsibleUserId).HasValue
                && visibility.VisibleUserIds.Contains((item.VisibilityResponsibleUserId ?? item.ResponsibleUserId)!.Value));
            return scope is null ? query : scope.FilterRows(query);
        }

        var opening = from.HasValue && from.Value.Date > DateTime.MinValue.Date
            ? await Rows(null, from.Value.Date.AddDays(-1))
                .GroupBy(item => new
                {
                    item.CompanyId,
                    item.MoneyAccountId,
                    item.Source,
                    Currency = item.Currency == null || item.Currency.Trim() == "" ? null : item.Currency.Trim().ToUpper(),
                    LegacyName = item.MoneyAccountId.HasValue ? "" : item.AccountName ?? ""
                })
                .Select(group => new MoneyDailyAggregate
                {
                    CompanyId = group.Key.CompanyId,
                    AccountId = group.Key.MoneyAccountId,
                    Source = group.Key.Source,
                    Currency = group.Key.Currency,
                    LegacyName = group.Key.LegacyName,
                    Label = group.Max(item => item.MoneyAccountName ?? item.AccountName) ?? "Счёт не определён",
                    Income = group.Where(item => item.Direction == "Income").Sum(item => item.Amount),
                    Expense = group.Where(item => item.Direction == "Expense").Sum(item => item.Amount),
                }).Take(maxBalanceRows + 1).ToListAsync(cancellationToken)
            : new List<MoneyDailyAggregate>();
        var movements = await Rows(from, to)
            .GroupBy(item => new
            {
                item.CompanyId,
                item.MoneyAccountId,
                item.Source,
                Date = item.Date.Date,
                Currency = item.Currency == null || item.Currency.Trim() == "" ? null : item.Currency.Trim().ToUpper(),
                LegacyName = item.MoneyAccountId.HasValue ? "" : item.AccountName ?? ""
            })
            .Select(group => new MoneyDailyAggregate
            {
                CompanyId = group.Key.CompanyId,
                AccountId = group.Key.MoneyAccountId,
                Source = group.Key.Source,
                Currency = group.Key.Currency,
                LegacyName = group.Key.LegacyName,
                Date = group.Key.Date,
                Label = group.Max(item => item.MoneyAccountName ?? item.AccountName) ?? "Счёт не определён",
                Income = group.Where(item => item.Direction == "Income").Sum(item => item.Amount),
                Expense = group.Where(item => item.Direction == "Expense").Sum(item => item.Amount),
                IncomeCount = group.Count(item => item.Direction == "Income"),
                ExpenseCount = group.Count(item => item.Direction == "Expense"),
            }).Take(maxBalanceRows + 1).ToListAsync(cancellationToken);

        if (opening.Count > maxBalanceRows || movements.Count > maxBalanceRows)
            throw new ArgumentException("Сократите период или выберите отдельный счёт для сверки.");

        var starting = opening.ToDictionary(item => item.Partition);
        var grouped = movements.GroupBy(item => item.Partition).ToDictionary(group => group.Key, group => group.ToList());
        var fillDays = from.HasValue && to.HasValue && (scope is not null || from.Value.Date == to.Value.Date);
        var keys = (fillDays ? grouped.Keys.Union(starting.Keys) : grouped.Keys).ToArray();
        var days = fillDays ? Math.Max(0, (to!.Value.Date - from!.Value.Date).Days + 1) : 0;
        if (fillDays && (long)keys.Length * days > maxBalanceRows)
            throw new ArgumentException("Сократите период или выберите отдельный счёт для сверки.");
        var result = new List<MoneyOperationDailyBalanceDto>();
        foreach (var key in keys)
        {
            starting.TryGetValue(key, out var initial);
            var byDate = grouped.TryGetValue(key, out var group) ? group.ToDictionary(item => item.Date) : new Dictionary<DateTime, MoneyDailyAggregate>();
            var label = group?.FirstOrDefault()?.Label ?? initial?.Label ?? "Счёт не определён";
            var running = (initial?.Income ?? 0m) - (initial?.Expense ?? 0m);
            var dates = fillDays
                ? Enumerable.Range(0, days).Select(offset => from!.Value.Date.AddDays(offset))
                : byDate.Keys.Order();
            var parsedSource = ParseEnum<MoneyOperationSource>(key.Source);
            var balanceKey = MoneyBalanceIdentity.Create(key.CompanyId, key.AccountId, parsedSource, key.Currency, key.LegacyName);
            foreach (var date in dates)
            {
                byDate.TryGetValue(date, out var movement);
                var income = movement?.Income ?? 0m;
                var expense = movement?.Expense ?? 0m;
                var closing = running + income - expense;
                result.Add(new MoneyOperationDailyBalanceDto
                {
                    CompanyId = key.CompanyId,
                    MoneyAccountId = key.AccountId,
                    Currency = key.Currency,
                    BalanceKey = balanceKey,
                    Date = date,
                    Source = parsedSource,
                    AccountName = label,
                    OpeningBalance = running,
                    Income = income,
                    Expense = expense,
                    ClosingBalance = closing,
                    IncomeCount = movement?.IncomeCount ?? 0,
                    ExpenseCount = movement?.ExpenseCount ?? 0,
                    IsBalanced = true,
                });
                running = closing;
            }
        }
        return result.OrderBy(item => item.Date).ThenBy(item => item.AccountName)
            .ThenBy(item => item.CompanyId).ThenBy(item => item.MoneyAccountId).ThenBy(item => item.Currency).ThenBy(item => item.Source).ToList();
    }

    public IQueryable<MoneyOperationQueryRow> Build(
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search)
    {
        var readableInvoiceOperations = _sources.ReadableInvoiceOperations;
        var readableContracts = _sources.ReadableContracts ?? _db.Contracts.Where(_ => false);
        var readableActs = _sources.ReadableActs ?? _db.Acts.Where(_ => false);
        var branches = BankStatementImportAccess.UndeletedCompanyBranches(_db);
        // One reference query per document type supplies both projection and
        // search. It never materializes documents or changes their stored links.
        var contractReferences =
            from payment in _sources.PaymentOperations
            join contract in readableContracts on payment.ContractId equals (int?)contract.Id
            where contract.SellerCompanyId.HasValue
                && (payment.SellerCompanyId.HasValue
                    ? contract.SellerCompanyId == payment.SellerCompanyId
                        && (!payment.SellerBranchId.HasValue || branches.Any(branch => branch.Id == payment.SellerBranchId
                            && branch.LegalEntityId == contract.SellerCompanyId))
                    : branches.Any(branch => branch.Id == payment.SellerBranchId && branch.LegalEntityId == contract.SellerCompanyId))
            select new MoneyContractReference
            {
                PaymentId = payment.Id,
                InvoiceId = payment.InvoiceId,
                Kind = payment.DocumentKind,
                CompanyId = contract.SellerCompanyId,
                ContractId = (int?)contract.Id,
                Number = contract.Number,
                Title = contract.Title,
            };
        var actReferences =
            from act in readableActs
            join payment in _sources.PaymentOperations on act.InvoiceId equals (int?)payment.Id
            where act.Status != ActStatus.Terminated && act.SellerCompanyId.HasValue
                && (payment.SellerCompanyId.HasValue
                    ? act.SellerCompanyId == payment.SellerCompanyId
                        && (!payment.SellerBranchId.HasValue || branches.Any(branch => branch.Id == payment.SellerBranchId
                            && branch.LegalEntityId == act.SellerCompanyId))
                    : branches.Any(branch => branch.Id == payment.SellerBranchId && branch.LegalEntityId == act.SellerCompanyId))
            select new MoneyActReference
            {
                PaymentId = payment.Id,
                InvoiceId = payment.InvoiceId,
                CompanyId = act.SellerCompanyId,
                ActId = (int?)act.Id,
                Number = act.Number,
                Date = act.Date,
                Status = act.Status,
            };
        var preserveInheritedMoneyVisibility = _sources.PreserveInheritedVisibility;
        var filteredCustomOperations = ApplyMoneyOperationFilters(
                _sources.CustomOperations,
                from,
                to,
                source,
                direction,
                accountingType,
                moneyAccountId,
                accountName,
                categoryName,
                search,
                readableInvoiceOperations,
                contractReferences);
        var customOperations =
            from operation in filteredCustomOperations
            join readable in readableInvoiceOperations on operation.Id equals readable.Id into invoiceReferences
            from readable in invoiceReferences.DefaultIfEmpty()
            select new MoneyOperationQueryRow
            {
                Id = operation.Id,
                Source = operation.Source == MoneyOperationSource.Cash
                    ? nameof(MoneyOperationSource.Cash)
                    : operation.Source == MoneyOperationSource.Card
                        ? nameof(MoneyOperationSource.Card)
                        : operation.Source == MoneyOperationSource.EMoney
                            ? nameof(MoneyOperationSource.EMoney)
                            : nameof(MoneyOperationSource.Bank),
                Direction = operation.Direction == MoneyOperationDirection.Income
                    ? nameof(MoneyOperationDirection.Income)
                    : operation.Direction == MoneyOperationDirection.Expense
                        ? nameof(MoneyOperationDirection.Expense)
                        : nameof(MoneyOperationDirection.Transfer),
                AccountingType = operation.AccountingType == MoneyOperationAccountingType.Fact
                    ? nameof(MoneyOperationAccountingType.Fact)
                    : nameof(MoneyOperationAccountingType.Plan),
                CompanyId = _db.MoneyAccounts.Where(account => account.Id == operation.MoneyAccountId)
                    .Select(account => account.CompanyId).FirstOrDefault(),
                MoneyAccountId = operation.MoneyAccountId,
                MoneyAccountName = operation.MoneyAccount != null ? operation.MoneyAccount.Name : null,
                AccountName = operation.AccountName,
                CounterpartyName = operation.CounterpartyName,
                // This unexposed value preserves the money row's existing visibility when document metadata is redacted.
                VisibilityResponsibleUserId = preserveInheritedMoneyVisibility ? (operation.ResponsibleUserId ?? _db.Payments
                    .Where(payment => operation.LinkedInvoiceId.HasValue && payment.InvoiceId == operation.LinkedInvoiceId.Value)
                    .SelectMany(payment => payment.Responsibles)
                    .OrderBy(link => link.UserId)
                    .Select(link => (int?)link.UserId)
                    .FirstOrDefault()) : null,
                ResponsibleUserId = operation.ResponsibleUserId ?? _db.Payments
                    .Where(payment => readable.LinkedInvoiceId.HasValue && payment.InvoiceId == readable.LinkedInvoiceId.Value)
                    .SelectMany(payment => payment.Responsibles)
                    .OrderBy(link => link.UserId)
                    .Select(link => (int?)link.UserId)
                    .FirstOrDefault(),
                ResponsibleName = operation.ResponsibleUser != null
                    ? (operation.ResponsibleUser.FullName != null && operation.ResponsibleUser.FullName != ""
                        ? operation.ResponsibleUser.FullName
                        : operation.ResponsibleUser.Email)
                    : _db.Payments
                    .Where(payment => readable.LinkedInvoiceId.HasValue && payment.InvoiceId == readable.LinkedInvoiceId.Value)
                    .SelectMany(payment => payment.Responsibles)
                    .OrderBy(link => link.UserId)
                    .Select(link => link.User.FullName != null && link.User.FullName != "" ? link.User.FullName : link.User.Email)
                    .FirstOrDefault(),
                IncomeTypeId = operation.IncomeTypeId,
                CategoryName = operation.IncomeType != null ? operation.IncomeType.Name : operation.CategoryName,
                Amount = operation.Amount,
                VatRate = operation.VatRate,
                VatAmount = operation.VatAmount,
                Currency = operation.Currency,
                PaymentOrderNumber = operation.PaymentOrderNumber,
                Date = operation.Date,
                RecognitionDate = operation.RecognitionDate,
                IsAdvancePayment = operation.IsAdvancePayment,
                Purpose = operation.Purpose,
                PaidInvoiceNumber = operation.PaidInvoiceNumber,
                PaidAmount = operation.PaidAmount,
                ProjectName = operation.ProjectName,
                DirectionName = operation.DirectionName,
                SalesPointName = operation.SalesPointName,
                PaymentPriority = operation.PaymentPriority,
                PaidAccountName = operation.PaidAccountName,
                LinkedInvoiceId = readable.LinkedInvoiceId,
                IsInvoiceLinkProtected = operation.LinkedInvoiceId.HasValue && !readable.LinkedInvoiceId.HasValue,
                LinkedInvoiceNumber = readable.LinkedInvoiceId.HasValue && operation.LinkedInvoice != null ? operation.LinkedInvoice.Number : null,
                LinkedInvoiceDate = readable.LinkedInvoiceId.HasValue && operation.LinkedInvoice != null ? operation.LinkedInvoice.Date : null,
                LinkedContractId = contractReferences
                    .Where(reference => reference.Kind == DocumentKind.Invoice && readable.LinkedInvoiceId.HasValue
                        && reference.InvoiceId == readable.LinkedInvoiceId && operation.MoneyAccount != null
                        && reference.CompanyId == operation.MoneyAccount.CompanyId)
                    .OrderBy(reference => reference.PaymentId).Select(reference => reference.ContractId).FirstOrDefault(),
                LinkedContractNumber = contractReferences
                    .Where(reference => reference.Kind == DocumentKind.Invoice && readable.LinkedInvoiceId.HasValue
                        && reference.InvoiceId == readable.LinkedInvoiceId && operation.MoneyAccount != null
                        && reference.CompanyId == operation.MoneyAccount.CompanyId)
                    .OrderBy(reference => reference.PaymentId).Select(reference => reference.Number).FirstOrDefault(),
                LinkedActId = actReferences
                    .Where(reference => readable.LinkedInvoiceId.HasValue && reference.InvoiceId == readable.LinkedInvoiceId
                        && operation.MoneyAccount != null && reference.CompanyId == operation.MoneyAccount.CompanyId)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => reference.ActId).FirstOrDefault(),
                LinkedActNumber = actReferences
                    .Where(reference => readable.LinkedInvoiceId.HasValue && reference.InvoiceId == readable.LinkedInvoiceId
                        && operation.MoneyAccount != null && reference.CompanyId == operation.MoneyAccount.CompanyId)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => reference.Number).FirstOrDefault(),
                LinkedActDate = actReferences
                    .Where(reference => readable.LinkedInvoiceId.HasValue && reference.InvoiceId == readable.LinkedInvoiceId
                        && operation.MoneyAccount != null && reference.CompanyId == operation.MoneyAccount.CompanyId)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => (DateTime?)reference.Date).FirstOrDefault(),
                MatchStatus = operation.MatchStatus == MoneyOperationMatchStatus.Matched
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : operation.MatchStatus == MoneyOperationMatchStatus.ClarificationRequired
                        ? nameof(MoneyOperationMatchStatus.ClarificationRequired)
                        : operation.MatchStatus == MoneyOperationMatchStatus.Ignored
                            ? nameof(MoneyOperationMatchStatus.Ignored)
                            : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = operation.MatchedAt,
                MatchComment = operation.MatchComment,
                AttachmentCount = operation.BankStatementImportBatchId.HasValue ? 1 : null,
                AttachmentSummary = operation.BankStatementImportBatch != null ? operation.BankStatementImportBatch.FileName : null,
                CreatedAt = operation.CreatedAt,
                UpdatedAt = operation.UpdatedAt,
                IsReadonly = false,
                RelatedPaymentId = null,
                CashOrderId = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (int?)order.Id)
                    .FirstOrDefault(),
                CashOrderNumber = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Number)
                    .FirstOrDefault(),
                CashOrderDate = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (DateTime?)order.Date)
                    .FirstOrDefault(),
                CashOrderStatus = _db.CashOrders
                    .Where(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Status == CashOrderStatus.Posted
                        ? nameof(CashOrderStatus.Posted)
                        : order.Status == CashOrderStatus.Draft
                            ? nameof(CashOrderStatus.Draft)
                            : nameof(CashOrderStatus.Cancelled))
                    .FirstOrDefault(),
                CanCreateCashOrder =
                    operation.Source == MoneyOperationSource.Cash &&
                    operation.AccountingType == MoneyOperationAccountingType.Fact &&
                    operation.Amount > 0m &&
                    (operation.Direction == MoneyOperationDirection.Income ||
                     operation.Direction == MoneyOperationDirection.Expense) &&
                    !_db.CashOrders.Any(order => order.MoneyOperationId == operation.Id && order.Status != CashOrderStatus.Cancelled),
            };

        var paymentOperations = ApplyPaymentFilters(
                _sources.PaymentOperations,
                from,
                to,
                source,
                direction,
                accountingType,
                moneyAccountId,
                accountName,
                categoryName,
                search,
                contractReferences)
            .Select(payment => new MoneyOperationQueryRow
            {
                Id = -payment.Id,
                Source =
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken))
                        ? nameof(MoneyOperationSource.Cash)
                        : payment.PaymentSource != null &&
                          payment.PaymentSource.Name != null &&
                          (payment.PaymentSource.Name.Contains(CardSourceToken) ||
                           payment.PaymentSource.Name.Contains(CardSourceTitleToken))
                            ? nameof(MoneyOperationSource.Card)
                            : payment.PaymentSource != null &&
                              payment.PaymentSource.Name != null &&
                              (payment.PaymentSource.Name.Contains(EMoneySourceToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) ||
                               payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))
                                ? nameof(MoneyOperationSource.EMoney)
                                : nameof(MoneyOperationSource.Bank),
                Direction = payment.Type == PaymentType.Income
                    ? nameof(MoneyOperationDirection.Income)
                    : nameof(MoneyOperationDirection.Expense),
                AccountingType = payment.Status == PaymentStatus.Completed
                    ? nameof(MoneyOperationAccountingType.Fact)
                    : nameof(MoneyOperationAccountingType.Plan),
                VisibilityResponsibleUserId = null,
                CompanyId = payment.SellerCompanyId ?? _db.CompanyBranches
                    .Where(branch => branch.Id == payment.SellerBranchId)
                    .Select(branch => (int?)branch.LegalEntityId).FirstOrDefault(),
                MoneyAccountId = null,
                MoneyAccountName = null,
                AccountName = payment.Account,
                CounterpartyName = payment.CounterpartyName,
                ResponsibleUserId = payment.Responsibles
                    .OrderBy(link => link.UserId)
                    .Select(link => (int?)link.UserId)
                    .FirstOrDefault(),
                ResponsibleName = payment.Responsibles
                    .OrderBy(link => link.UserId)
                    .Select(link => link.User.FullName != null && link.User.FullName != "" ? link.User.FullName : link.User.Email)
                    .FirstOrDefault(),
                IncomeTypeId = payment.IncomeTypeId,
                CategoryName = payment.IncomeType != null
                    ? payment.IncomeType.Name
                    : payment.ClientCase != null
                        ? payment.ClientCase.Title
                        : null,
                Amount =
                    payment.Status == PaymentStatus.Completed && payment.PaidAmount > 0m
                        ? payment.PaidAmount
                        : payment.Amount,
                VatRate = null,
                VatAmount = null,
                Currency = "RUB",
                PaymentOrderNumber = null,
                Date = payment.Status == PaymentStatus.Completed
                    ? (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date)
                    : payment.Date,
                RecognitionDate = payment.Date,
                IsAdvancePayment = false,
                Purpose = payment.Description,
                PaidInvoiceNumber = payment.Account,
                PaidAmount = payment.PaidAmount,
                ProjectName = payment.ClientCase != null ? payment.ClientCase.Title : null,
                DirectionName = payment.PaymentSource != null ? payment.PaymentSource.Name : null,
                SalesPointName = null,
                PaymentPriority = null,
                PaidAccountName = payment.Account,
                LinkedInvoiceId = payment.InvoiceId,
                IsInvoiceLinkProtected = false,
                LinkedInvoiceNumber = payment.Invoice != null ? payment.Invoice.Number : payment.Account,
                LinkedInvoiceDate = payment.Invoice != null ? payment.Invoice.Date : payment.AccountDate,
                LinkedContractId = contractReferences.Where(reference => reference.PaymentId == payment.Id)
                    .Select(reference => reference.ContractId).FirstOrDefault(),
                LinkedContractNumber = contractReferences.Where(reference => reference.PaymentId == payment.Id)
                    .Select(reference => reference.Number).FirstOrDefault(),
                LinkedActId = actReferences.Where(reference => reference.PaymentId == payment.Id)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => reference.ActId).FirstOrDefault(),
                LinkedActNumber = actReferences.Where(reference => reference.PaymentId == payment.Id)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => reference.Number).FirstOrDefault(),
                LinkedActDate = actReferences.Where(reference => reference.PaymentId == payment.Id)
                    .OrderByDescending(reference => reference.Status == ActStatus.Signed).ThenByDescending(reference => reference.Date)
                    .ThenByDescending(reference => reference.ActId).Select(reference => (DateTime?)reference.Date).FirstOrDefault(),
                MatchStatus = payment.InvoiceId.HasValue
                    ? nameof(MoneyOperationMatchStatus.Matched)
                    : nameof(MoneyOperationMatchStatus.None),
                MatchedAt = payment.LastPaymentDate ?? payment.PaidDate,
                MatchComment = null,
                AttachmentCount = null,
                AttachmentSummary = null,
                CreatedAt = payment.CreatedAt,
                UpdatedAt = null,
                IsReadonly = true,
                RelatedPaymentId = payment.Id,
                CashOrderId = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (int?)order.Id)
                    .FirstOrDefault(),
                CashOrderNumber = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Number)
                    .FirstOrDefault(),
                CashOrderDate = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => (DateTime?)order.Date)
                    .FirstOrDefault(),
                CashOrderStatus = _db.CashOrders
                    .Where(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled)
                    .OrderByDescending(order => order.Id)
                    .Select(order => order.Status == CashOrderStatus.Posted
                        ? nameof(CashOrderStatus.Posted)
                        : order.Status == CashOrderStatus.Draft
                            ? nameof(CashOrderStatus.Draft)
                            : nameof(CashOrderStatus.Cancelled))
                    .FirstOrDefault(),
                CanCreateCashOrder =
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken)) &&
                    !_db.CashOrders.Any(order => order.PaymentId == payment.Id && order.Status != CashOrderStatus.Cancelled),
            });

        var combinedQuery = customOperations.Concat(paymentOperations);

        return combinedQuery;
    }

    private static IQueryable<MoneyOperation> ApplyMoneyOperationFilters(
        IQueryable<MoneyOperation> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search,
        IQueryable<MoneyOperation> readableInvoiceOperations,
        IQueryable<MoneyContractReference> contractReferences)
    {
        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(operation => operation.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(operation => operation.Date <= toDate);
        }

        if (source.HasValue)
        {
            query = query.Where(operation => operation.Source == source.Value);
        }

        if (direction.HasValue)
        {
            query = query.Where(operation => operation.Direction == direction.Value);
        }

        if (accountingType.HasValue)
        {
            query = query.Where(operation => operation.AccountingType == accountingType.Value);
        }

        if (moneyAccountId.HasValue)
        {
            query = query.Where(operation => operation.MoneyAccountId == moneyAccountId.Value);
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var accountTerm = accountName.Trim();
            query = query.Where(operation => operation.AccountName == accountTerm);
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var categoryTerm = categoryName.Trim();
            var categoryTitleTerm = ToTitleSearchTerm(categoryTerm);
            query = query.Where(operation =>
                (operation.CategoryName ?? string.Empty).Contains(categoryTerm) ||
                (operation.IncomeType != null && operation.IncomeType.Name.Contains(categoryTerm)) ||
                (categoryTitleTerm != categoryTerm &&
                    ((operation.CategoryName ?? string.Empty).Contains(categoryTitleTerm) ||
                     (operation.IncomeType != null && operation.IncomeType.Name.Contains(categoryTitleTerm)))));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(operation =>
                (operation.AccountName ?? string.Empty).Contains(term) ||
                (operation.CounterpartyName ?? string.Empty).Contains(term) ||
                (operation.Purpose ?? string.Empty).Contains(term) ||
                (operation.PaidInvoiceNumber ?? string.Empty).Contains(term) ||
                (operation.CategoryName ?? string.Empty).Contains(term) ||
                (operation.IncomeType != null && operation.IncomeType.Name.Contains(term)) ||
                (operation.ResponsibleUser != null &&
                    ((operation.ResponsibleUser.FullName ?? string.Empty).Contains(term) ||
                     operation.ResponsibleUser.Email.Contains(term))) ||
                (operation.LinkedInvoice != null && readableInvoiceOperations.Any(readable => readable.Id == operation.Id) && operation.LinkedInvoice.Number.Contains(term)) ||
                (operation.BankStatementImportBatch != null && operation.BankStatementImportBatch.FileName.Contains(term)) ||
                (operation.LinkedInvoice != null && readableInvoiceOperations.Any(readable => readable.Id == operation.Id) && operation.LinkedInvoice.Payments.Any(payment =>
                    contractReferences.Any(reference => reference.PaymentId == payment.Id
                        && operation.MoneyAccount != null && reference.CompanyId == operation.MoneyAccount.CompanyId
                        && ((reference.Number ?? string.Empty).Contains(term) || (reference.Title ?? string.Empty).Contains(term))) ||
                    payment.Responsibles.Any(link =>
                        (link.User.FullName ?? string.Empty).Contains(term) ||
                        link.User.Email.Contains(term)))) ||
                (titleTerm != term &&
                    ((operation.AccountName ?? string.Empty).Contains(titleTerm) ||
                     (operation.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (operation.Purpose ?? string.Empty).Contains(titleTerm) ||
                     (operation.PaidInvoiceNumber ?? string.Empty).Contains(titleTerm) ||
                      (operation.CategoryName ?? string.Empty).Contains(titleTerm) ||
                      (operation.IncomeType != null && operation.IncomeType.Name.Contains(titleTerm)) ||
                      (operation.ResponsibleUser != null &&
                          ((operation.ResponsibleUser.FullName ?? string.Empty).Contains(titleTerm) ||
                           operation.ResponsibleUser.Email.Contains(titleTerm))) ||
                      (operation.LinkedInvoice != null && readableInvoiceOperations.Any(readable => readable.Id == operation.Id) && operation.LinkedInvoice.Number.Contains(titleTerm)) ||
                     (operation.BankStatementImportBatch != null && operation.BankStatementImportBatch.FileName.Contains(titleTerm)) ||
                     (operation.LinkedInvoice != null && readableInvoiceOperations.Any(readable => readable.Id == operation.Id) && operation.LinkedInvoice.Payments.Any(payment =>
                         contractReferences.Any(reference => reference.PaymentId == payment.Id
                        && operation.MoneyAccount != null && reference.CompanyId == operation.MoneyAccount.CompanyId
                        && ((reference.Number ?? string.Empty).Contains(titleTerm) || (reference.Title ?? string.Empty).Contains(titleTerm))) ||
                         payment.Responsibles.Any(link =>
                             (link.User.FullName ?? string.Empty).Contains(titleTerm) ||
                             link.User.Email.Contains(titleTerm)))))));
        }

        return query;
    }

    private static IQueryable<Payment> ApplyPaymentFilters(
        IQueryable<Payment> query,
        DateTime? from,
        DateTime? to,
        MoneyOperationSource? source,
        MoneyOperationDirection? direction,
        MoneyOperationAccountingType? accountingType,
        int? moneyAccountId,
        string? accountName,
        string? categoryName,
        string? search,
        IQueryable<MoneyContractReference> contractReferences)
    {
        query = query.Where(payment =>
            payment.Status == PaymentStatus.Completed &&
            payment.DocumentKind != DocumentKind.Invoice &&
            payment.InvoiceId == null);

        if (moneyAccountId.HasValue)
        {
            query = query.Where(_ => false);
        }

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(payment => (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(payment => (payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date) <= toDate);
        }

        if (source.HasValue)
        {
            query = source.Value switch
            {
                MoneyOperationSource.Cash => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CashSourceToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) ||
                     payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken))),
                MoneyOperationSource.Card => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(CardSourceToken) ||
                     payment.PaymentSource.Name.Contains(CardSourceTitleToken))),
                MoneyOperationSource.EMoney => query.Where(payment =>
                    payment.PaymentSource != null &&
                    payment.PaymentSource.Name != null &&
                    (payment.PaymentSource.Name.Contains(EMoneySourceToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) ||
                     payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))),
                MoneyOperationSource.Bank => query.Where(payment =>
                    payment.PaymentSource == null ||
                    payment.PaymentSource.Name == null ||
                    (!payment.PaymentSource.Name.Contains(CashSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeToken) &&
                     !payment.PaymentSource.Name.Contains(CashSourceAlternativeTitleToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceToken) &&
                     !payment.PaymentSource.Name.Contains(CardSourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceCompactToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceCompactTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceRussianToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceRussianTitleToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYuMoneyToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYooMoneyToken) &&
                     !payment.PaymentSource.Name.Contains(EMoneySourceYandexToken))),
                _ => query.Where(_ => false),
            };
        }

        if (direction.HasValue)
        {
            var mappedType = direction.Value == MoneyOperationDirection.Income
                ? PaymentType.Income
                : direction.Value == MoneyOperationDirection.Expense
                    ? PaymentType.Expense
                    : (PaymentType?)null;

            if (mappedType.HasValue)
            {
                query = query.Where(payment => payment.Type == mappedType.Value);
            }
            else
            {
                query = query.Where(_ => false);
            }
        }

        if (accountingType.HasValue)
        {
            query = accountingType.Value == MoneyOperationAccountingType.Fact
                ? query.Where(payment => payment.Status == PaymentStatus.Completed)
                : query.Where(payment => payment.Status != PaymentStatus.Completed);
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var accountTerm = accountName.Trim();
            query = query.Where(payment => payment.Account == accountTerm);
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var categoryTerm = categoryName.Trim();
            var categoryTitleTerm = ToTitleSearchTerm(categoryTerm);
            query = query.Where(payment =>
                (payment.IncomeType != null && payment.IncomeType.Name.Contains(categoryTerm)) ||
                (payment.ClientCase != null && (payment.ClientCase.Title ?? string.Empty).Contains(categoryTerm)) ||
                (categoryTitleTerm != categoryTerm &&
                    ((payment.IncomeType != null && payment.IncomeType.Name.Contains(categoryTitleTerm)) ||
                     (payment.ClientCase != null && (payment.ClientCase.Title ?? string.Empty).Contains(categoryTitleTerm)))));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var titleTerm = ToTitleSearchTerm(term);
            query = query.Where(payment =>
                (payment.CounterpartyName ?? string.Empty).Contains(term) ||
                (payment.Description ?? string.Empty).Contains(term) ||
                (payment.Account ?? string.Empty).Contains(term) ||
                (payment.IncomeType != null && payment.IncomeType.Name.Contains(term)) ||
                (payment.Invoice != null && payment.Invoice.Number.Contains(term)) ||
                contractReferences.Any(reference => reference.PaymentId == payment.Id
                    && ((reference.Number ?? string.Empty).Contains(term) || (reference.Title ?? string.Empty).Contains(term))) ||
                payment.Responsibles.Any(link =>
                    (link.User.FullName ?? string.Empty).Contains(term) ||
                    link.User.Email.Contains(term)) ||
                (titleTerm != term &&
                    ((payment.CounterpartyName ?? string.Empty).Contains(titleTerm) ||
                     (payment.Description ?? string.Empty).Contains(titleTerm) ||
                     (payment.Account ?? string.Empty).Contains(titleTerm) ||
                     (payment.IncomeType != null && payment.IncomeType.Name.Contains(titleTerm)) ||
                     (payment.Invoice != null && payment.Invoice.Number.Contains(titleTerm)) ||
                     contractReferences.Any(reference => reference.PaymentId == payment.Id
                    && ((reference.Number ?? string.Empty).Contains(titleTerm) || (reference.Title ?? string.Empty).Contains(titleTerm))) ||
                     payment.Responsibles.Any(link =>
                         (link.User.FullName ?? string.Empty).Contains(titleTerm) ||
                         link.User.Email.Contains(titleTerm)))));
        }

        return query;
    }

    private static string ToTitleSearchTerm(string value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]));

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, out var parsed) ? parsed : default;




    private sealed class MoneyContractReference
    {
        public int PaymentId { get; init; }
        public int? InvoiceId { get; init; }
        public DocumentKind Kind { get; init; }
        public int? CompanyId { get; init; }
        public int? ContractId { get; init; }
        public string? Number { get; init; }
        public string? Title { get; init; }
    }

    private sealed class MoneyActReference
    {
        public int PaymentId { get; init; }
        public int? InvoiceId { get; init; }
        public int? CompanyId { get; init; }
        public int? ActId { get; init; }
        public string? Number { get; init; }
        public DateTime Date { get; init; }
        public ActStatus Status { get; init; }
    }

    private sealed record MoneyBalancePartition(int? CompanyId, int? AccountId, string Source, string? Currency, string LegacyName);
    private sealed class MoneyDailyAggregate
    {
        public int? CompanyId { get; init; }
        public int? AccountId { get; init; }
        public string Source { get; init; } = "";
        public string? Currency { get; init; }
        public string LegacyName { get; init; } = "";
        public string Label { get; init; } = "";
        public DateTime Date { get; init; }
        public decimal Income { get; init; }
        public decimal Expense { get; init; }
        public int IncomeCount { get; init; }
        public int ExpenseCount { get; init; }
        public MoneyBalancePartition Partition => new(CompanyId, AccountId, Source, Currency, LegacyName);
    }
}
