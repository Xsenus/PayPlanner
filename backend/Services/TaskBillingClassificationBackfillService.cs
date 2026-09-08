using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class TaskBillingClassificationBackfillService
{
    public static async Task<int> EnsureLegacyTasksClassifiedAsync(PaymentContext db, CancellationToken ct = default)
    {
        var legacyTasks = await db.TaskJournalItems
            .Where(task => task.ClientBillingClassification == TaskClientBillingClassification.Legacy)
            .ToListAsync(ct);
        if (legacyTasks.Count == 0)
        {
            return 0;
        }

        var clientIds = legacyTasks
            .Where(task => task.ClientId.HasValue)
            .Select(task => task.ClientId!.Value)
            .ToHashSet();
        var legalEntityIds = legacyTasks
            .Where(task => task.LegalEntityId.HasValue)
            .Select(task => task.LegalEntityId!.Value)
            .ToHashSet();
        var today = DateTime.UtcNow.Date;
        var contracts = await db.Contracts
            .AsNoTracking()
            .Include(contract => contract.ClientContracts)
            .Include(contract => contract.LegalEntityContracts)
            .Where(contract => contract.Status != ContractStatus.Cancelled && contract.Status != ContractStatus.Terminated)
            .Where(contract => contract.Date <= today)
            .Where(contract => !contract.ValidUntil.HasValue || contract.ValidUntil.Value >= today)
            .Where(contract =>
                contract.ClientContracts.Any(link => clientIds.Contains(link.ClientId)) ||
                contract.LegalEntityContracts.Any(link => legalEntityIds.Contains(link.LegalEntityId)))
            .OrderByDescending(contract => contract.Status == ContractStatus.Concluded)
            .ThenByDescending(contract => contract.Date)
            .ThenByDescending(contract => contract.Id)
            .ToListAsync(ct);

        var clientContracts = new Dictionary<int, Contract>();
        var legalEntityContracts = new Dictionary<int, Contract>();
        foreach (var contract in contracts)
        {
            foreach (var link in contract.ClientContracts)
            {
                if (clientIds.Contains(link.ClientId))
                {
                    clientContracts.TryAdd(link.ClientId, contract);
                }
            }

            foreach (var link in contract.LegalEntityContracts)
            {
                if (legalEntityIds.Contains(link.LegalEntityId))
                {
                    legalEntityContracts.TryAdd(link.LegalEntityId, contract);
                }
            }
        }

        var classifiedAt = DateTime.UtcNow;
        foreach (var task in legacyTasks)
        {
            if (!task.ClientId.HasValue && !task.LegalEntityId.HasValue)
            {
                task.ClientBillingClassification = TaskClientBillingClassification.Internal;
                task.BillingContractId = null;
                task.BillingModeSnapshot = null;
                task.BillingDecisionAt = classifiedAt;
                continue;
            }

            var contract = task.ClientId is { } clientId && clientContracts.TryGetValue(clientId, out var clientContract)
                ? clientContract
                : task.LegalEntityId is { } legalEntityId && legalEntityContracts.TryGetValue(legalEntityId, out var legalEntityContract)
                    ? legalEntityContract
                    : null;
            task.ClientBillingClassification = TaskClientBillingClassification.Billable;
            task.BillingContractId = contract?.Id;
            task.BillingModeSnapshot = contract is null ? null : ContractBillingMode.Normalize(contract.BillingMode);
            task.BillingDecisionAt = classifiedAt;
        }

        db.SetAuditReason("Автоматическое распределение старых задач по категориям оплаты.");
        await db.SaveChangesAsync(ct);
        return legacyTasks.Count;
    }
}
