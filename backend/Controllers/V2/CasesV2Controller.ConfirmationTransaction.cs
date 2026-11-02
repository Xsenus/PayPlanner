using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private async Task<ActionResult<T>> RunCaseConfirmationAsync<T>(
        int id, Func<ClientCase, Task<ActionResult<T>>> confirm, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || actorId is not > 0)
            return Unauthorized();
        BalanceApplicationLease? lease = null;
        try
        {
            // Keep the actor, permissions, source and appended history in one
            // clean transaction. A previous tracked read is not write authority.
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return Unauthorized();
            _casesAccessTask = null;
            if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
                return forbidden;
            var visibility = await ResolveCasesVisibilityAsync(ct);
            if (visibility is null)
                return MissingCasesVisibilityResult();
            var clientCase = await _db.ClientCases.ApplyClientCaseVisibility(visibility)
                .FirstOrDefaultAsync(item => item.Id == id, ct);
            if (clientCase is null)
                return NotFound();
            if (_db.Database.IsNpgsql())
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
            var result = await confirm(clientCase);
            if (result.Result is OkObjectResult)
                await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Дело или права доступа изменены либо сейчас заняты. Обновите карточку перед подтверждением." });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }
}
