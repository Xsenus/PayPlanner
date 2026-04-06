using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private async Task RestoreDerivedClientContractsAsync(
        PaymentContext target,
        DatabaseImportTableResultDto table,
        CancellationToken cancellationToken)
    {
        var existingKeys = (await target.ClientContracts
                .AsNoTracking()
                .Select(link => new { link.ContractId, link.ClientId })
                .ToListAsync(cancellationToken))
            .Select(link => Key(link.ContractId, link.ClientId))
            .ToHashSet(StringComparer.Ordinal);

        var directPaymentPairs = await target.Payments
            .AsNoTracking()
            .Where(payment => payment.ContractId.HasValue && payment.ClientId.HasValue)
            .Select(payment => new { ContractId = payment.ContractId!.Value, ClientId = payment.ClientId!.Value })
            .ToListAsync(cancellationToken);

        var contractPaymentPairs = await (
                from contractPayment in target.ContractPayments.AsNoTracking()
                join payment in target.Payments.AsNoTracking() on contractPayment.PaymentId equals payment.Id
                where payment.ClientId.HasValue
                select new { contractPayment.ContractId, ClientId = payment.ClientId!.Value })
            .ToListAsync(cancellationToken);

        var contractInvoicePairs = await (
                from contractInvoice in target.ContractInvoices.AsNoTracking()
                join payment in target.Payments.AsNoTracking() on contractInvoice.InvoiceId equals payment.Id
                where payment.ClientId.HasValue
                select new { contractInvoice.ContractId, ClientId = payment.ClientId!.Value })
            .ToListAsync(cancellationToken);

        var contractActPairs = await (
                from contractAct in target.ContractActs.AsNoTracking()
                join act in target.Acts.AsNoTracking() on contractAct.ActId equals act.Id
                where act.ClientId.HasValue
                select new { contractAct.ContractId, ClientId = act.ClientId!.Value })
            .ToListAsync(cancellationToken);

        var candidatePairs = directPaymentPairs
            .Concat(contractPaymentPairs)
            .Concat(contractInvoicePairs)
            .Concat(contractActPairs)
            .Where(pair => pair.ContractId > 0 && pair.ClientId > 0)
            .GroupBy(pair => Key(pair.ContractId, pair.ClientId), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        table.SourceRows = candidatePairs.Count;

        foreach (var pair in candidatePairs)
        {
            var key = Key(pair.ContractId, pair.ClientId);
            if (!existingKeys.Add(key))
            {
                table.Skipped += 1;
                continue;
            }

            target.ClientContracts.Add(new ClientContract
            {
                ContractId = pair.ContractId,
                ClientId = pair.ClientId,
                Comment = "Automatically restored from related import documents."
            });
            table.Inserted += 1;
        }

        if (table.Inserted > 0)
        {
            await SaveImportedChangesAsync(target, cancellationToken);
        }
    }
}
