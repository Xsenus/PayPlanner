using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private static DateTime NormalizeDateTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
    };

    private static void NormalizeTrackedDateTimes(PaymentContext target)
    {
        if (!target.Database.IsNpgsql())
        {
            return;
        }

        foreach (var entry in target.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTime dateTimeValue)
                {
                    property.CurrentValue = NormalizeDateTime(dateTimeValue);
                }
            }
        }
    }

    private static string GetDisplayErrorMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private static Task SaveImportedChangesAsync(PaymentContext target, CancellationToken cancellationToken)
    {
        NormalizeTrackedDateTimes(target);
        return target.SaveChangesAsync(cancellationToken);
    }
}
