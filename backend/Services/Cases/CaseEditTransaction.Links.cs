using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

internal static partial class CaseEditTransaction
{
    public static async Task<IActionResult?> ValidateEditedLinksAsync(PaymentContext db, ClientCase current, ClientCase requested,
        DataVisibilityAccess visibility, CancellationToken ct)
    {
        var hasClient = requested.ClientId is > 0;
        var hasLegalEntity = requested.LegalEntityId is > 0;
        if (hasClient == hasLegalEntity || (!hasClient && requested.ClientId.HasValue) || (!hasLegalEntity && requested.LegalEntityId.HasValue))
            return new BadRequestObjectResult("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        var changedOwner = current.ClientId != requested.ClientId || current.LegalEntityId != requested.LegalEntityId;
        if (changedOwner)
        {
            if (visibility.SeesAll && db.Database.IsNpgsql())
            {
                if (hasClient) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {requested.ClientId!.Value} FOR SHARE NOWAIT", ct);
                else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {requested.LegalEntityId!.Value} FOR SHARE NOWAIT", ct);
            }
            if (hasClient && !await db.Clients.AsNoTracking().ApplyClientVisibility(visibility).AnyAsync(row => row.Id == requested.ClientId!.Value, ct))
                return new BadRequestObjectResult($"Unknown clientId {requested.ClientId!.Value}");
            if (hasLegalEntity && !await db.LegalEntities.AsNoTracking().ApplyLegalEntityVisibility(visibility).AnyAsync(row => row.Id == requested.LegalEntityId!.Value, ct))
                return new BadRequestObjectResult($"Unknown legalEntityId {requested.LegalEntityId!.Value}");
            if (!visibility.SeesAll) await LockVisibleOwnersAsync(db, requested, ct);
        }
        if (current.ResponsibleUserId != requested.ResponsibleUserId && requested.ResponsibleUserId is int responsible)
        {
            await LockUsersAsync(db, [responsible], ct);
            if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == responsible && row.IsActive, ct))
                return new BadRequestObjectResult("Ответственный недоступен или отключён. Обновите список сотрудников.");
        }
        return null;
    }
}
