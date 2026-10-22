using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private sealed record InvoiceImportKeys(Dictionary<int, string> Source, Dictionary<int, string> Target);

    private static async Task<InvoiceImportKeys> BuildSafeInvoiceImportKeysAsync(
        PaymentContext source, PaymentContext target, CancellationToken ct)
    {
        if (await source.Payments.HasAmbiguousInvoiceLinksAsync(ct))
            throw new InvalidOperationException(InvoiceQueryExtensions.ConflictMessage);
        if (await source.Acts.AnyAsync(act => act.InvoiceId.HasValue && (act.Invoice == null
                || (act.ClientId.HasValue && act.ClientId != act.Invoice.ClientId)
                || (act.LegalEntityId.HasValue && act.LegalEntityId != act.Invoice.LegalEntityId)
                || (act.SellerCompanyId.HasValue && act.SellerCompanyId != act.Invoice.SellerCompanyId)
                || act.SellerBranchId.HasValue), ct))
            throw new InvalidOperationException("Связь акта со счетом или филиалом требует уточнения. Данные не изменены.");

        var sourceInvoices = await source.Invoices.AsNoTracking().Include(invoice => invoice.Payments).ToListAsync(ct);
        var numbers = sourceInvoices.Select(invoice => NormalizeText(invoice.Number)).Distinct().ToList();
        var uids = sourceInvoices.SelectMany(invoice => invoice.Payments)
            .Select(payment => SpreadsheetImportService.ExtractOneCExternalId(payment.Notes))
            .Where(uid => uid is not null).Distinct().ToArray();
        var uidMarkers = uids.SelectMany(uid => new[] { "UID 1С: " + uid, "UID 1C: " + uid }).ToArray();
        var targetInvoices = await target.Invoices.AsNoTracking().Where(invoice => numbers.Contains(invoice.Number.Trim().ToUpper())
                || invoice.Payments.Any(payment => payment.Notes != null && uidMarkers.Any(marker => payment.Notes.ToUpper().Contains(marker))))
            .Include(invoice => invoice.Payments).ToListAsync(ct);
        targetInvoices = targetInvoices.Where(invoice => numbers.Contains(NormalizeText(invoice.Number))
            || invoice.Payments.Any(payment => uids.Contains(SpreadsheetImportService.ExtractOneCExternalId(payment.Notes)))).ToList();
        if (targetInvoices.Any(invoice => invoice.Payments.Count(payment => payment.DocumentKind == DocumentKind.Invoice) > 1))
            throw new InvalidOperationException(InvoiceQueryExtensions.ConflictMessage);
        var targetIds = targetInvoices.Select(invoice => invoice.Id).ToList();
        if (await target.Payments.Where(payment => payment.InvoiceId.HasValue && targetIds.Contains(payment.InvoiceId.Value))
            .HasAmbiguousInvoiceLinksAsync(ct))
            throw new InvalidOperationException(InvoiceQueryExtensions.ConflictMessage);

        var sourceKeys = await BuildInvoiceOwnershipKeysAsync(source, sourceInvoices, ct);
        var targetKeys = await BuildInvoiceOwnershipKeysAsync(target, targetInvoices, ct);
        if (sourceKeys.Values.GroupBy(key => key, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || targetKeys.Values.GroupBy(key => key, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Найдено несколько счетов с одинаковыми реквизитами и владельцем. Импорт остановлен до записи данных; требуется уточнение документа.");

        var targetByKey = targetInvoices.ToDictionary(invoice => targetKeys[invoice.Id], StringComparer.Ordinal);
        foreach (var invoice in sourceInvoices)
            if (targetByKey.TryGetValue(sourceKeys[invoice.Id], out var existing)
                && (InvoiceFinancialFingerprint(invoice) != InvoiceFinancialFingerprint(existing)
                    || NormalizeText(invoice.Number) != NormalizeText(existing.Number) || invoice.Date != existing.Date))
                throw new InvalidOperationException("Реквизиты или состояние оплаты существующего счета отличаются от загружаемой базы. Импорт остановлен до записи, чтобы не заменить финансовую историю.");

        var sourceActs = await source.Acts.AsNoTracking().ToListAsync(ct);
        var actNumbers = sourceActs.Select(act => act.Number).Distinct().ToList();
        var targetActs = await target.Acts.AsNoTracking().Where(act => actNumbers.Contains(act.Number)).ToListAsync(ct);
        if (sourceActs.GroupBy(BuildRawActIdentity).Any(group => group.Count() > 1)
            || targetActs.GroupBy(BuildRawActIdentity).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Найдено несколько актов с одинаковой принадлежностью и реквизитами. Импорт остановлен до записи данных.");
        return new InvoiceImportKeys(sourceKeys, targetKeys);
    }

    private static string BuildRawActIdentity(Act act) => Key(act.Number, act.Date, act.ClientId, act.LegalEntityId,
        act.SellerCompanyId, act.SellerBranchId, act.InvoiceId, SpreadsheetImportService.ExtractOneCExternalId(act.Comment));

    private static string InvoiceFinancialFingerprint(Invoice invoice)
        => string.Join(";", invoice.Payments
            .Select(payment => Key(payment.DocumentKind, payment.Type, payment.Amount, payment.PaidAmount, payment.Status, payment.IsPaid,
                payment.PaidDate, payment.LastPaymentDate, payment.Date, payment.PlannedDate))
            .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal));

    private static async Task<Dictionary<int, string>> BuildInvoiceOwnershipKeysAsync(
        PaymentContext db, List<Invoice> invoices, CancellationToken ct)
    {
        var payments = invoices.SelectMany(invoice => invoice.Payments).ToList();
        // Branches have no import map in this legacy database importer. Never silently discard their identity.
        if (payments.Any(payment => payment.SellerBranchId.HasValue))
            throw new InvalidOperationException("Импорт счетов с филиалами требует сопоставления филиалов. Данные не изменены.");
        var clientIds = payments.Where(payment => payment.ClientId.HasValue).Select(payment => payment.ClientId!.Value).Distinct().ToList();
        var legalIds = payments.SelectMany(payment => new[] { payment.LegalEntityId, payment.SellerCompanyId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var clients = (await db.Clients.AsNoTracking().Where(client => clientIds.Contains(client.Id)).ToListAsync(ct))
            .ToDictionary(client => client.Id, BuildClientKey);
        var companies = (await db.LegalEntities.AsNoTracking().Where(company => legalIds.Contains(company.Id)).ToListAsync(ct))
            .ToDictionary(company => company.Id, BuildLegalEntityKey);
        string OwnerKey(Dictionary<int, string> owners, int? id)
            => id.HasValue ? owners.TryGetValue(id.Value, out var key) ? key
                : throw new InvalidOperationException("Не найден владелец счета. Данные не изменены.") : string.Empty;

        var result = new Dictionary<int, string>();
        foreach (var invoice in invoices)
        {
            var documents = invoice.Payments.Where(payment => payment.DocumentKind == DocumentKind.Invoice).ToList();
            if (documents.Count == 0) documents = invoice.Payments.ToList();
            var owners = documents.Select(payment => Key(OwnerKey(clients, payment.ClientId), OwnerKey(companies, payment.LegalEntityId),
                payment.Type, OwnerKey(companies, payment.SellerCompanyId))).Distinct(StringComparer.Ordinal).ToList();
            var uids = documents.Select(payment => SpreadsheetImportService.ExtractOneCExternalId(payment.Notes))
                .Where(uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).ToList();
            if (owners.Count > 1 || uids.Count > 1)
                throw new InvalidOperationException(InvoiceQueryExtensions.ConflictMessage);
            // Neither a database-local ID nor the document number alone identifies the owner across databases.
            var identity = uids.Count == 1 ? "uid:" + uids[0] : Key(invoice.Number, invoice.Date);
            result.Add(invoice.Id, Key(identity, owners.SingleOrDefault() ?? "unowned"));
        }
        return result;
    }
}
