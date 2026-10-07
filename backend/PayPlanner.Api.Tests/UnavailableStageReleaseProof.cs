using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PayPlanner.Api.Tests;

// An unknown stage commit is distinct from a lost reply with a readable,
// exact durable receipt. Block only the first fresh receipt read; cleanup
// can subsequently confirm the committed release without publishing data.
internal sealed class UnavailableStageReleaseProof(Func<bool> enabled) : DbCommandInterceptor
{
    public int Interceptions { get; private set; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Interceptions == 0 && enabled()
            && command.CommandText.Contains("StorageCapacityReservations", StringComparison.Ordinal))
        {
            Interceptions++;
            throw new IOException("Simulated unavailable stage release confirmation before publication.");
        }
        return ValueTask.FromResult(result);
    }
}
