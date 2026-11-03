using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientDeletionRules
{
    // One bounded aggregate for the selected clients. Include deleted business
    // records so restoring history cannot restore a reference to a deleted client.
    public static async Task<IReadOnlyDictionary<int, List<string>>> UsageAsync(PaymentContext db, int[] ids, CancellationToken ct)
    {
        var rows = await db.Clients.AsNoTracking().Where(client => ids.Contains(client.Id)).Select(client => new
        {
            client.Id,
            Payments = db.Payments.IgnoreQueryFilters().Any(row => row.ClientId == client.Id || row.ExpenseClientId == client.Id),
            Acts = db.Acts.IgnoreQueryFilters().Any(row => row.ClientId == client.Id),
            Cases = db.ClientCases.IgnoreQueryFilters().Any(row => row.ClientId == client.Id),
            Contracts = db.ClientContracts.Any(row => row.ClientId == client.Id),
            Powers = db.PowersOfAttorney.IgnoreQueryFilters().Any(row => row.ClientId == client.Id),
            Tasks = db.TaskJournalItems.IgnoreQueryFilters().Any(row => row.ClientId == client.Id),
            Requests = db.SalesRequests.Any(row => row.ClientId == client.Id),
            Balance = db.ClientBalanceTransactions.Any(row => row.ClientId == client.Id) || db.BalanceApplicationReceipts.Where(row => row.ClientId.HasValue && ids.Contains(row.ClientId.Value)).Select(row => row.ClientId).Contains(client.Id),
            Cash = db.CashOrders.IgnoreQueryFilters().Any(row => row.ClientId == client.Id),
            Parties = db.ClientCaseRelatedParties.Any(row => row.ClientId == client.Id),
            Portal = db.ClientPortalAccounts.Any(row => row.ClientId == client.Id),
            Messenger = db.MessengerRecipientBindings.Any(row => row.ClientId == client.Id) || db.MessengerDocumentDeliveries.Any(row => row.ClientId == client.Id),
            Signatures = db.ElectronicSignatureEnvelopes.Any(row => row.ClientId == client.Id),
        }).ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row =>
        {
            var usage = new List<string>(13);
            if (row.Payments) usage.Add("платежи");
            if (row.Acts) usage.Add("акты");
            if (row.Cases) usage.Add("дела клиента");
            if (row.Contracts) usage.Add("договоры");
            if (row.Powers) usage.Add("доверенности");
            if (row.Tasks) usage.Add("задачи");
            if (row.Requests) usage.Add("заявки");
            if (row.Balance) usage.Add("остатки и зачёты");
            if (row.Cash) usage.Add("кассовые ордера");
            if (row.Parties) usage.Add("участие в делах");
            if (row.Portal) usage.Add("личный кабинет клиента");
            if (row.Messenger) usage.Add("доставка документов");
            if (row.Signatures) usage.Add("электронные подписи");
            return usage;
        });
    }

    public static async Task RemoveProfileAsync(PaymentContext db, int[] ids, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"AddressRecords\" WHERE \"ClientId\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientPassports\" WHERE \"ClientId\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"LinkedClientId\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
        }
        var addresses = await db.AddressRecords.Where(row => row.ClientId.HasValue && ids.Contains(row.ClientId.Value)).ToListAsync(ct);
        db.AddressRecords.RemoveRange(addresses);
        var passports = await db.ClientPassports.Where(row => ids.Contains(row.ClientId)).ToListAsync(ct);
        db.ClientPassports.RemoveRange(passports);
        // Preserve the existing quick-contact demotion rule for an unused profile.
        var contacts = await db.ExternalContacts.Where(row => row.LinkedClientId.HasValue && ids.Contains(row.LinkedClientId.Value)).ToListAsync(ct);
        foreach (var contact in contacts) contact.LinkedClientId = null;
    }
}
