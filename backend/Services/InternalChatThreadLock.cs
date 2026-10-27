using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

/// <summary>Serializes chat publication and history boundaries within an existing transaction.</summary>
internal static class InternalChatThreadLock
{
    public static async Task<bool> AcquireAsync(PaymentContext db, int threadId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Chat thread admission requires an active transaction.");
        return await db.InternalChatThreads.Where(item => item.Id == threadId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct) == 1;
    }
}
