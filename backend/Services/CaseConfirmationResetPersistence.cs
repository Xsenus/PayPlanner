using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

internal static class CaseConfirmationResetPersistence
{
    public static void Apply(PaymentContext db, ClientCase clientCase)
    {
        var entry = db.Entry(clientCase);
        if (entry.State is EntityState.Added or EntityState.Detached or EntityState.Deleted)
            throw new InvalidOperationException("Confirmation resets require an existing tracked case.");

        if (!string.Equals(entry.Property(item => item.Strategy).OriginalValue, clientCase.Strategy, StringComparison.Ordinal))
        {
            clientCase.PositionReviewedAt = null;
            clientCase.PositionReviewedByUserId = null;
            clientCase.PositionReviewedByName = string.Empty;
            // Another request may have confirmed the old source after this read.
            // Include even originally-null columns in the same UPDATE as the source.
            entry.Property(item => item.PositionReviewedAt).IsModified = true;
            entry.Property(item => item.PositionReviewedByUserId).IsModified = true;
            entry.Property(item => item.PositionReviewedByName).IsModified = true;
        }

        if (entry.Property(item => item.NextHearingAt).OriginalValue != clientCase.NextHearingAt)
        {
            clientCase.NextHearingConfirmedForAt = null;
            clientCase.NextHearingConfirmedAt = null;
            clientCase.NextHearingConfirmedByUserId = null;
            clientCase.NextHearingConfirmedByName = string.Empty;
            entry.Property(item => item.NextHearingConfirmedForAt).IsModified = true;
            entry.Property(item => item.NextHearingConfirmedAt).IsModified = true;
            entry.Property(item => item.NextHearingConfirmedByUserId).IsModified = true;
            entry.Property(item => item.NextHearingConfirmedByName).IsModified = true;
        }
    }
}
