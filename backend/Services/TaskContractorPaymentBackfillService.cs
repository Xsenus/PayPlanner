using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class TaskContractorPaymentBackfillService
{
    private const string ContractorPaymentMarker = "[PayPlanner:TaskContractorPayment]";

    public static async Task<int> EnsureGeneratedPaymentDetailsAsync(PaymentContext db, CancellationToken ct = default)
    {
        var tasks = await db.TaskJournalItems
            .Include(task => task.ContractorPayment)
                .ThenInclude(payment => payment!.Responsibles)
            .Where(task => task.ContractorPaymentEnabled && task.ContractorPaymentId.HasValue)
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        foreach (var task in tasks)
        {
            var payment = task.ContractorPayment;
            if (payment is null || !IsGeneratedContractorPayment(task, payment))
            {
                continue;
            }

            if (EnsurePaymentDetails(task, payment))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static bool EnsurePaymentDetails(TaskJournalItem task, Payment payment)
    {
        var changed = false;
        var assigneeName = Normalize(task.AssigneeName);

        if (string.IsNullOrWhiteSpace(payment.CounterpartyName) && assigneeName is not null)
        {
            payment.CounterpartyName = assigneeName;
            changed = true;
        }

        if (payment.Type != PaymentType.Expense)
        {
            payment.Type = PaymentType.Expense;
            changed = true;
        }

        if (payment.ClientId.HasValue)
        {
            payment.ClientId = null;
            changed = true;
        }

        if (payment.LegalEntityId.HasValue)
        {
            payment.LegalEntityId = null;
            changed = true;
        }

        if (payment.ExpenseClientId != task.ClientId)
        {
            payment.ExpenseClientId = task.ClientId;
            changed = true;
        }

        if (payment.ExpenseLegalEntityId != task.LegalEntityId)
        {
            payment.ExpenseLegalEntityId = task.LegalEntityId;
            changed = true;
        }

        if (payment.ClientCaseId != task.ClientCaseId)
        {
            payment.ClientCaseId = task.ClientCaseId;
            changed = true;
        }

        if (task.AssigneeUserId.HasValue
            && !payment.Responsibles.Any(item => item.UserId == task.AssigneeUserId.Value))
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = task.AssigneeUserId.Value,
            });
            changed = true;
        }

        return changed;
    }

    private static bool IsGeneratedContractorPayment(TaskJournalItem task, Payment payment)
    {
        if (ContainsMarker(payment.SystemNotes, task.Id))
        {
            return true;
        }

        return payment.Description.Contains("Оплата исполнителю", StringComparison.OrdinalIgnoreCase)
            && payment.Notes.Contains("Автоматически создано из задачи", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMarker(string? systemNotes, int taskId)
        => !string.IsNullOrWhiteSpace(systemNotes)
           && systemNotes.Contains($"{ContractorPaymentMarker}:{taskId}", StringComparison.Ordinal);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
