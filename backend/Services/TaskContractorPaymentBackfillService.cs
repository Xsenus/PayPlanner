using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class TaskContractorPaymentBackfillService
{
    private const string ContractorPaymentMarker = "[PayPlanner:TaskContractorPayment]";
    private const string ContractorPaymentAutoNote = "\u0410\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0447\u0435\u0441\u043a\u0438 \u0441\u043e\u0437\u0434\u0430\u043d\u043e \u0438\u0437 \u0437\u0430\u0434\u0430\u0447\u0438.";
    private const string ContractorPaymentSystemNote = "\u0410\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0447\u0435\u0441\u043a\u0438\u0439 \u043f\u043b\u0430\u0442\u0435\u0436 \u0438\u0441\u043f\u043e\u043b\u043d\u0438\u0442\u0435\u043b\u044e \u0438\u0437 \u0437\u0430\u0434\u0430\u0447\u0438.";
    private const string ContractorPaymentDescriptionPrefix = "\u041e\u043f\u043b\u0430\u0442\u0430 \u0438\u0441\u043f\u043e\u043b\u043d\u0438\u0442\u0435\u043b\u044e \u043f\u043e \u0437\u0430\u0434\u0430\u0447\u0435";

    public static async Task<int> EnsureGeneratedPaymentDetailsAsync(PaymentContext db, CancellationToken ct = default)
    {
        var tasks = await db.TaskJournalItems
            .Include(task => task.ContractorPayment)
                .ThenInclude(payment => payment!.Responsibles)
            .Where(task => task.ContractorPaymentEnabled)
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            return 0;
        }

        var externalContractorIds = await db.Users
            .AsNoTracking()
            .Where(user => user.IsExternalContractor)
            .Select(user => user.Id)
            .ToListAsync(ct);
        var externalContractorIdSet = externalContractorIds.ToHashSet();

        var changed = 0;
        foreach (var task in tasks)
        {
            var payment = task.ContractorPayment;
            if (payment is null)
            {
                if (!TryCreateMissingGeneratedPayment(task, externalContractorIdSet, out var createdPayment))
                {
                    continue;
                }

                db.Payments.Add(createdPayment);
                task.ContractorPayment = createdPayment;
                changed++;
                continue;
            }

            if (!IsGeneratedContractorPayment(task, payment))
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

    private static bool TryCreateMissingGeneratedPayment(
        TaskJournalItem task,
        IReadOnlySet<int> externalContractorIds,
        out Payment payment)
    {
        payment = null!;

        if (!ShouldCreateGeneratedPayment(task, externalContractorIds))
        {
            return false;
        }

        var dueDate = ResolveMissingPaymentDueDate(task);
        if (dueDate is null)
        {
            return false;
        }

        var amount = Math.Round(task.ContractorPaymentAmount!.Value, 2);
        var assigneeName = Normalize(task.AssigneeName) ?? "\u0418\u0441\u043f\u043e\u043b\u043d\u0438\u0442\u0435\u043b\u044c";
        payment = new Payment
        {
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Amount = amount,
            PaidAmount = 0m,
            Date = dueDate.Value,
            PlannedDate = dueDate.Value,
            Description = BuildContractorPaymentDescription(task),
            CounterpartyName = assigneeName,
            Notes = ContractorPaymentAutoNote,
            SystemNotes = string.Join('\n', new[]
            {
                BuildContractorPaymentMarker(task.Id),
                ContractorPaymentSystemNote,
            }),
            ClientId = null,
            LegalEntityId = null,
            ExpenseClientId = task.ClientId,
            ExpenseLegalEntityId = task.LegalEntityId,
            ClientCaseId = task.ClientCaseId,
            DocumentKind = DocumentKind.Payment,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            IsPaid = false,
        };

        payment.Responsibles.Add(new PaymentResponsible
        {
            UserId = task.AssigneeUserId!.Value,
        });

        return true;
    }

    private static bool ShouldCreateGeneratedPayment(TaskJournalItem task, IReadOnlySet<int> externalContractorIds)
    {
        if (!task.ContractorPaymentEnabled
            || task.ContractorPaymentId.HasValue
            || !task.AssigneeUserId.HasValue
            || !externalContractorIds.Contains(task.AssigneeUserId.Value)
            || !task.ContractorPaymentAmount.HasValue
            || Math.Round(task.ContractorPaymentAmount.Value, 2) <= 0m)
        {
            return false;
        }

        if (!task.ContractorPaymentOnResult)
        {
            return task.ContractorPaymentDueDate.HasValue;
        }

        return string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase)
            && task.ContractorPaymentResultDelayDays is >= 0 and <= 365;
    }

    private static DateTime? ResolveMissingPaymentDueDate(TaskJournalItem task)
    {
        if (!task.ContractorPaymentOnResult)
        {
            return task.ContractorPaymentDueDate?.Date;
        }

        if (task.ContractorPaymentResultDelayDays is not >= 0 and <= 365)
        {
            return null;
        }

        return (task.UpdatedAt ?? task.CreatedAt).Date.AddDays(task.ContractorPaymentResultDelayDays.GetValueOrDefault());
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

        return payment.Description.Contains(ContractorPaymentDescriptionPrefix, StringComparison.OrdinalIgnoreCase)
            && payment.Notes.Contains(ContractorPaymentAutoNote, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMarker(string? systemNotes, int taskId)
        => !string.IsNullOrWhiteSpace(systemNotes)
           && systemNotes.Contains($"{ContractorPaymentMarker}:{taskId}", StringComparison.Ordinal);

    private static string BuildContractorPaymentMarker(int taskId)
        => $"{ContractorPaymentMarker}:{taskId}";

    private static string BuildContractorPaymentDescription(TaskJournalItem task)
    {
        var title = Normalize(task.Title) is { } normalizedTitle
            ? $"\u00ab{normalizedTitle}\u00bb"
            : $"#{task.Id}";
        return Truncate($"{ContractorPaymentDescriptionPrefix} {title}", 500);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
