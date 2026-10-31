using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class MoneyOperationsController
{
    private async Task<IActionResult> RunMoneyInvoiceMutationAsync(bool hasInvoice, Func<Task<IActionResult>> write, CancellationToken ct)
    {
        if (!hasInvoice)
        {
            await using var mutation = await MoneyMutationScope.BeginAsync(_db, ct);
            var response = await write();
            if (response is OkObjectResult or CreatedResult or NoContentResult) await mutation.CommitAsync(ct);
            return response;
        }
        BalanceApplicationLease? lease = null;
        try
        {
            if (GetCurrentUserId() is not > 0) return Unauthorized();
            lease = await BalanceApplicationLease.BeginAsync(_db, GetCurrentUserId()!.Value, ct);
            if (_db.Database.IsNpgsql()) await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            using var credit = new PaymentCreditInvariantScope(_db);
            var response = await write();
            if (response is OkObjectResult or CreatedResult or NoContentResult)
            {
                if (!await credit.KeepsAppliedCreditAsync(ct)) return BadRequest(PaymentCreditInvariantScope.RejectionMessage);
                await lease.CommitAsync(ct);
            }
            return response;
        }
        catch (MoneyInvoiceAllocationException error) { return BadRequest(error.Message); }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        { return Conflict(FinancialConcurrencyGuard.ConflictMessage); }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}
