using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services;

/// <summary>
/// Общий расчёт оплаты счёта из денежной операции, включая его платежи рассрочки.
/// Проверка прав, повторного применения и транзакция выполняются вызывающим механизмом.
/// </summary>
public static class MoneyOperationInvoiceAllocationService
{
    private const int MaximumRelatedPayments = 5000;
    // Existing CRM invoices are denominated in rubles. A missing operation
    // currency is not evidence that the bank movement was in rubles.
    public static bool HasConfirmedRubleCurrency(MoneyOperation operation)
        => string.Equals(operation.Currency?.Trim(), "RUB", StringComparison.OrdinalIgnoreCase);

    public static async Task ApplyAsync(
        PaymentContext db,
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default,
        IReadOnlyCollection<Payment>? loadedPayments = null)
    {
        if (operation.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense) || operation.Amount <= 0m)
        {
            return;
        }
        if (!HasConfirmedRubleCurrency(operation))
            throw new InvalidOperationException("Валюта денежной операции не подтверждена как рубли; сверка со счётом недоступна.");

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Invoice allocation requires the caller's transaction.");
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries<Payment>().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new MoneyInvoiceAllocationException("В контексте уже есть несохранённые изменения платежей. Сначала завершите предыдущее действие.");
        if (operation.Id <= 0 || !await db.MoneyOperations.AsNoTracking().AnyAsync(row => row.Id == operation.Id, ct))
            throw new MoneyInvoiceAllocationException("Денежная операция ещё не сохранена для сверки.");
        var graph = db.Payments.Where(payment => payment.InvoiceId == invoiceId);
        var ids = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
        if (ids.Length == 0 || ids.Length > MaximumRelatedPayments)
            throw new MoneyInvoiceAllocationException("Состав платежей счёта требует отдельной сверки; неполное распределение не выполнялось.");
        if (loadedPayments is not null && !ids.SequenceEqual(loadedPayments.Select(payment => payment.Id).Order()))
            throw new MoneyInvoiceAllocationException("Передан неполный или изменённый граф платежей счёта.");
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
        else if (db.Database.IsSqlite())
            await graph.ExecuteUpdateAsync(set => set.SetProperty(payment => payment.Date, payment => payment.Date), ct);
        else throw new NotSupportedException("Invoice allocation supports SQLite and PostgreSQL only.");
        var currentIds = await graph.AsNoTracking().OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(MaximumRelatedPayments + 1).ToArrayAsync(ct);
        if (!ids.SequenceEqual(currentIds)) throw new DbUpdateConcurrencyException("Invoice allocation graph changed.");
        if (await graph.HasAmbiguousInvoiceLinksAsync(ct)) throw new MoneyInvoiceAllocationException(InvoiceQueryExtensions.ConflictMessage);
        if (await PaymentCreditInvariantScope.CancellationProtectedPayments(db).AnyAsync(payment => ids.Contains(payment.Id), ct))
            throw new MoneyInvoiceAllocationException(PaymentCreditInvariantScope.CancellationProtectedMessage);
        var overpaymentKey = $"money-operation-overpayment:{operation.Id}";
        if (await db.ClientBalanceTransactions.AsNoTracking().AnyAsync(row => row.ExternalKey == overpaymentKey, ct))
            throw new MoneyInvoiceAllocationException("По денежной операции уже есть история переплаты. Повторное распределение требует сверки истории.");
        var targetType = operation.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income;
        var payments = await ReloadGraphAsync(db, graph, ct);
        var companyId = await db.MoneyAccounts.AsNoTracking().Where(account => account.Id == operation.MoneyAccountId).Select(account => account.CompanyId).SingleOrDefaultAsync(ct);
        if (companyId is not > 0 || payments.Any(payment => payment.Type != targetType || payment.SellerCompanyId != companyId))
            throw new MoneyInvoiceAllocationException("Денежная операция и все платежи счёта должны относиться к одной собственной компании и направлению.");
        var representative = payments.Where(item => item.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(item => item.Amount).ThenBy(item => item.Id).FirstOrDefault();
        if (representative is null) throw new MoneyInvoiceAllocationException("Основная запись счёта недоступна для сверки.");
        using var credit = new PaymentCreditInvariantScope(db);
        credit.IncludePayments(payments);

        var sources = InvoicePaymentStateService.SelectStatusSourcePayments(payments, representative.Id, representative.Amount);
        var remaining = Math.Round(operation.Amount, 2, MidpointRounding.AwayFromZero);
        var applied = 0m;
        Payment? lastApplied = null;
        var sourceLabel = operation.Source switch
        {
            MoneyOperationSource.Cash => "касса",
            MoneyOperationSource.Card => "карты",
            MoneyOperationSource.EMoney => "e-money",
            _ => "банк",
        };
        var movementLabel = targetType == PaymentType.Expense ? "списание" : "поступление";
        var now = DateTime.UtcNow;

        foreach (var payment in sources.OrderBy(InvoicePaymentStateService.EffectivePlannedDate).ThenBy(item => item.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (remaining <= 0m) break;
            var outstanding = InvoicePaymentStateService.ResolveOutstandingAmount(payment);
            var amount = Math.Round(Math.Min(remaining, outstanding), 2, MidpointRounding.AwayFromZero);
            if (amount <= 0m) continue;

            payment.PaidAmount = Math.Round(Math.Max(payment.PaidAmount, 0m) + amount, 2, MidpointRounding.AwayFromZero);
            payment.LastPaymentDate = operation.Date;
            payment.PaymentDocumentDate ??= operation.RecognitionDate ?? operation.Date;
            payment.PaymentOrderNumber ??= operation.PaymentOrderNumber;
            if (payment.PaidAmount + 0.009m >= payment.Amount)
            {
                payment.PaidAmount = payment.Amount;
                payment.Status = PaymentStatus.Completed;
                payment.IsPaid = true;
                payment.PaidDate ??= operation.Date;
            }
            else
            {
                payment.Status = PaymentStatus.Processing;
                payment.IsPaid = false;
                payment.PaidDate = null;
            }

            AppendNote(payment, $"Системно: {now:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}.");
            remaining -= amount;
            applied += amount;
            lastApplied = payment;
        }

        // Переплата возникает только после покрытия всех платежей счёта, а не первого взноса.
        var clientId = lastApplied?.ClientId ?? representative.ClientId;
        if (applied > 0m && targetType == PaymentType.Income && remaining > 0.009m && clientId.HasValue && operation.Id > 0)
        {
            var balance = new ClientBalanceTransaction { ExternalKey = overpaymentKey, CreatedAt = now };
            db.ClientBalanceTransactions.Add(balance);
            balance.ClientId = clientId.Value;
            balance.InvoiceId = invoiceId;
            balance.PaymentId = lastApplied!.Id;
            balance.Amount = remaining;
            balance.Type = ClientBalanceTransactionType.Overpayment;
            balance.Direction = ClientBalanceDirection.Credit;
            balance.Description = $"Переплата по денежной операции #{operation.Id}";
            AppendNote(lastApplied, $"Системно: {now:dd.MM.yyyy HH:mm:ss} — переплата {remaining.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента.");
        }

        if (applied > 0m)
        {
            InvoicePaymentStateService.ApplySnapshot(representative,
                InvoicePaymentStateService.CalculateRepresentativeState(representative, sources, now));
        }
        if (applied <= 0m || remaining > 0.009m && (targetType != PaymentType.Income || !clientId.HasValue))
            throw new MoneyInvoiceAllocationException("Остаток оплаты счёта изменился; распределение не выполнено.");
        await db.SaveChangesAsync(ct);
        if (!await credit.KeepsAppliedCreditAsync(ct)) throw new MoneyInvoiceAllocationException(PaymentCreditInvariantScope.RejectionMessage);
    }

    // One projection refreshes every mapped scalar, including hidden audit and
    // event tokens. Keep existing entity identities without N+1 Reload queries.
    private static async Task<List<Payment>> ReloadGraphAsync(PaymentContext db, IQueryable<Payment> graph, CancellationToken ct)
    {
        var properties = db.Model.FindEntityType(typeof(Payment))!.GetProperties().ToArray();
        var parameter = Expression.Parameter(typeof(Payment), "payment");
        var propertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
        var selector = Expression.Lambda<Func<Payment, object?[]>>(Expression.NewArrayInit(typeof(object), properties.Select(property =>
            Expression.Convert(Expression.Call(propertyMethod.MakeGenericMethod(property.ClrType), parameter, Expression.Constant(property.Name)), typeof(object)))), parameter);
        var snapshots = await graph.AsNoTracking().Select(selector).ToArrayAsync(ct);
        var idIndex = Array.FindIndex(properties, property => property.Name == nameof(Payment.Id));
        var tracked = db.ChangeTracker.Entries<Payment>().ToDictionary(entry => entry.Entity.Id);
        var payments = new List<Payment>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            var id = (int)snapshot[idIndex]!;
            if (!tracked.TryGetValue(id, out var entry)) { var payment = new Payment { Id = id }; entry = db.Attach(payment); }
            var values = entry.CurrentValues;
            for (var index = 0; index < properties.Length; index++) values[properties[index].Name] = snapshot[index];
            entry.OriginalValues.SetValues(values);
            entry.State = EntityState.Unchanged;
            payments.Add(entry.Entity);
        }
        return payments;
    }

    private static void AppendNote(Payment payment, string note)
        => payment.SystemNotes = string.IsNullOrWhiteSpace(payment.SystemNotes) ? note : $"{payment.SystemNotes.Trim()}\n{note}";
}

public sealed class MoneyInvoiceAllocationException(string message) : InvalidOperationException(message);
