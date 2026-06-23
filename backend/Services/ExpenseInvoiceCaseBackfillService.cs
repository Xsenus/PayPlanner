using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class ExpenseInvoiceCaseBackfillService
{
    private const string BackfillMarker = "[PayPlanner:ExpenseInvoiceCaseBackfill]";

    public static async Task<BackfillExpenseInvoiceCasesResponse> RunAsync(
        PaymentContext db,
        bool dryRun,
        int limit,
        IReadOnlyCollection<int>? paymentIds = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit <= 0 ? 1_000 : limit, 1, 50_000);
        var normalizedPaymentIds = paymentIds?
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet()
            ?? new HashSet<int>();

        var query = db.Payments
            .Include(payment => payment.ClientCase)
                .ThenInclude(clientCase => clientCase!.Client)
            .Where(payment =>
                payment.Type == PaymentType.Expense
                && payment.DocumentKind == DocumentKind.Invoice
                && payment.ClientCaseId.HasValue
                && payment.ClientCaseId.Value > 0);

        if (normalizedPaymentIds.Count > 0)
        {
            query = query.Where(payment => normalizedPaymentIds.Contains(payment.Id));
        }
        else
        {
            query = query.Where(payment => !payment.ExpenseClientId.HasValue);
        }

        var payments = await query
            .OrderBy(payment => payment.Id)
            .Take(limit)
            .ToListAsync(ct);

        var scanned = 0;
        var applied = 0;
        var skipped = 0;
        var items = new List<BackfillExpenseInvoiceCaseItemDto>();

        foreach (var payment in payments)
        {
            scanned++;
            var evaluation = Evaluate(payment, dryRun);
            items.Add(evaluation.Item);

            if (!evaluation.CanApply)
            {
                skipped++;
                continue;
            }

            if (dryRun)
            {
                continue;
            }

            payment.ExpenseClientId = evaluation.Item.SuggestedExpenseClientId;
            payment.SystemNotes = AppendBackfillMarker(payment.SystemNotes, payment.ClientCaseId!.Value);
            applied++;
        }

        if (!dryRun && applied > 0)
        {
            db.SetAuditReason($"Backfill расходных счетов по делам: заполнен клиент расхода у {applied} счетов.");
            await db.SaveChangesAsync(ct);
        }

        if (dryRun)
        {
            db.ChangeTracker.Clear();
        }

        return new BackfillExpenseInvoiceCasesResponse
        {
            DryRun = dryRun,
            Scanned = scanned,
            Changed = items.Count(item => item.Action == "WouldUpdate" || item.Action == "Updated"),
            Applied = applied,
            Skipped = skipped,
            Items = items,
        };
    }

    private static BackfillEvaluation Evaluate(Payment payment, bool dryRun)
    {
        if (payment.ClientCase is null)
        {
            return Skip(payment, "SkippedMissingCase", "Дело не найдено, счет не изменен.");
        }

        var suggestedClientId = payment.ClientCase.ClientId;
        if (!suggestedClientId.HasValue || suggestedClientId.Value <= 0)
        {
            return Skip(payment, "SkippedCaseWithoutClient", "У дела нет клиента-владельца, счет не изменен.");
        }

        if (payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value == suggestedClientId.Value)
        {
            return Skip(payment, "AlreadyLinked", "Клиент расхода уже совпадает с клиентом дела.");
        }

        if (payment.ExpenseClientId.HasValue)
        {
            return Skip(payment, "SkippedConflict", "Клиент расхода уже заполнен другим значением, требуется ручная проверка.");
        }

        return new BackfillEvaluation(
            CanApply: true,
            Item: new BackfillExpenseInvoiceCaseItemDto
            {
                PaymentId = payment.Id,
                InvoiceId = payment.InvoiceId,
                Account = payment.Account,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseTitle = payment.ClientCase.Title,
                CurrentExpenseClientId = payment.ExpenseClientId,
                SuggestedExpenseClientId = suggestedClientId,
                SuggestedExpenseClientName = payment.ClientCase.Client?.Name,
                Action = dryRun ? "WouldUpdate" : "Updated",
                Message = dryRun
                    ? "Клиент расхода будет заполнен по клиенту выбранного дела."
                    : "Клиент расхода заполнен по клиенту выбранного дела.",
            });
    }

    private static BackfillEvaluation Skip(Payment payment, string action, string message)
        => new(
            CanApply: false,
            Item: new BackfillExpenseInvoiceCaseItemDto
            {
                PaymentId = payment.Id,
                InvoiceId = payment.InvoiceId,
                Account = payment.Account,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseTitle = payment.ClientCase?.Title,
                CurrentExpenseClientId = payment.ExpenseClientId,
                SuggestedExpenseClientId = payment.ClientCase?.ClientId,
                SuggestedExpenseClientName = payment.ClientCase?.Client?.Name,
                Action = action,
                Message = message,
            });

    private static string AppendBackfillMarker(string? systemNotes, int clientCaseId)
    {
        var marker = $"{BackfillMarker}:client-case:{clientCaseId}";
        if (!string.IsNullOrWhiteSpace(systemNotes)
            && systemNotes.Contains(marker, StringComparison.Ordinal))
        {
            return systemNotes;
        }

        return string.IsNullOrWhiteSpace(systemNotes)
            ? marker
            : $"{systemNotes.Trim()}{Environment.NewLine}{marker}";
    }

    private sealed record BackfillEvaluation(bool CanApply, BackfillExpenseInvoiceCaseItemDto Item);
}
