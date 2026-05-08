using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class PaymentRemainderTransferBackfillService
{
    public static async Task<int> EnsureLegacyLinksAsync(PaymentContext db, CancellationToken ct = default)
    {
        var existingSourceIds = await db.PaymentRemainderTransfers
            .AsNoTracking()
            .Select(item => item.SourcePaymentId)
            .ToHashSetAsync(ct);

        var existingRemainderIds = await db.PaymentRemainderTransfers
            .AsNoTracking()
            .Select(item => item.RemainderPaymentId)
            .ToHashSetAsync(ct);

        var markerRows = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.SystemNotes != null
                              && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))
            .Select(payment => new
            {
                payment.Id,
                payment.SystemNotes,
            })
            .ToListAsync(ct);

        var parsedMarkers = markerRows
            .Select(row => new
            {
                RemainderPaymentId = row.Id,
                Parsed = PaymentRemainderTransferRules.TryReadMarker(row.SystemNotes, out var marker),
                Marker = marker,
            })
            .Where(row => row.Parsed && row.Marker.SourcePaymentId > 0)
            .ToList();

        if (parsedMarkers.Count == 0)
        {
            return 0;
        }

        var sourceIds = parsedMarkers
            .Select(row => row.Marker.SourcePaymentId)
            .Distinct()
            .ToList();

        var existingPaymentIds = await db.Payments
            .AsNoTracking()
            .Where(payment => sourceIds.Contains(payment.Id))
            .Select(payment => payment.Id)
            .ToHashSetAsync(ct);

        var now = DateTime.UtcNow;
        var created = 0;
        foreach (var row in parsedMarkers.OrderBy(item => item.RemainderPaymentId))
        {
            if (!existingPaymentIds.Contains(row.Marker.SourcePaymentId)
                || existingSourceIds.Contains(row.Marker.SourcePaymentId)
                || existingRemainderIds.Contains(row.RemainderPaymentId)
                || row.Marker.SourcePaymentId == row.RemainderPaymentId)
            {
                continue;
            }

            db.PaymentRemainderTransfers.Add(new PaymentRemainderTransfer
            {
                SourcePaymentId = row.Marker.SourcePaymentId,
                RemainderPaymentId = row.RemainderPaymentId,
                SourceOriginalAmount = row.Marker.SourceOriginalAmount,
                PaidAmount = row.Marker.PaidAmount,
                RemainderAmount = row.Marker.RemainderAmount,
                RemainderDate = row.Marker.RemainderDate.Date,
                CreatedAt = now,
            });
            existingSourceIds.Add(row.Marker.SourcePaymentId);
            existingRemainderIds.Add(row.RemainderPaymentId);
            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return created;
    }
}
