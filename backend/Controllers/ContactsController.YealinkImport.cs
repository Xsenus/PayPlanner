using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private async Task<ActionResult<YealinkContactImportResultDto>> RunYealinkImportAsync(
        IFormFile? file, bool dryRun, string? decisionsJson, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || actorId is not > 0) return Unauthorized();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await _db.Users.AsNoTracking().AnyAsync(x => x.Id == actorId.Value && x.IsActive && x.IsApproved, ct)) return Unauthorized();
                return Forbid();
            }
            // Retain the existing admin-only import policy, based on the current database role.
            if (!access.Visibility.IsAdmin || access.Permission is not { CanView: true, CanCreate: true }) return Forbid();
            var result = await ImportYealinkCoreAsync(file, dryRun, decisionsJson, access.Visibility, ct);
            if (!dryRun && result.Result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Контакты или права доступа изменены либо сейчас заняты. Импорт не сохранён; обновите предпросмотр перед повторной попыткой." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private async Task LockYealinkMatchAsync(string source, int id, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql()) return;
        if (source == "client")
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
        else
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
    }
}
