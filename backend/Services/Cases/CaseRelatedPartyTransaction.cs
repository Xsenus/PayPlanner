using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Cases;

internal sealed record CasePartyDirectorySource(string Kind, Client? Client, ExternalContact? Contact, LegalEntity? LegalEntity,
    DataVisibilityAccess CaseVisibility, DataVisibilityAccess? LegalVisibility);

internal static class CaseRelatedPartyTransaction
{
    public static async Task LockPartyAsync(PaymentContext db, int id, CancellationToken ct)
    {
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseRelatedParties\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
    }

    public static Task<IActionResult> AddAsync(PaymentContext db, ClaimsPrincipal actor, int id, string? kind, int sourceId,
        Func<ClientCase, CasePartyDirectorySource, Task<IActionResult>> write, CancellationToken ct)
        => EditAsync(db, actor, id, async (clientCase, caseAccess) =>
        {
            if (kind is not ("client" or "externalContact" or "legalEntity") || sourceId <= 0)
                return new BadRequestObjectResult(new { message = "Выберите связанное лицо или организацию из справочника." });
            var section = kind == "legalEntity" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients;
            var access = await ReadAccessAsync(db, actor, section, ct);
            var message = kind == "client" ? "Физическое лицо не найдено или недоступно." : kind == "externalContact" ? "Контакт не найден или недоступен." : "Организация не найдена или недоступна.";
            if (access is null) return new BadRequestObjectResult(new { message });
            Client? client = null;
            ExternalContact? contact = null;
            LegalEntity? legal = null;
            int? owner = null;
            int? linkedLegalId = null;
            if (kind == "client")
            {
                client = await db.Clients.AsNoTracking().ApplyClientVisibility(access.Visibility).Where(x => x.Id == sourceId)
                    .Select(x => new Client { Id = x.Id, Name = x.Name, Phone = x.Phone, Email = x.Email, Company = x.Company, LegalEntityId = x.LegalEntityId, ResponsibleUserId = x.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (client is null) return new BadRequestObjectResult(new { message });
                owner = client.ResponsibleUserId; linkedLegalId = client.LegalEntityId;
                if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {sourceId} FOR SHARE NOWAIT", ct);
            }
            else if (kind == "externalContact")
            {
                contact = await db.ExternalContacts.AsNoTracking().Where(x => x.Id == sourceId && (access.Visibility.SeesAll || x.ResponsibleUserId.HasValue && access.Visibility.VisibleUserIds.Contains(x.ResponsibleUserId.Value)))
                    .Select(x => new ExternalContact { Id = x.Id, FullName = x.FullName, Phone = x.Phone, Email = x.Email, LegalEntityId = x.LegalEntityId, ResponsibleUserId = x.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (contact is null) return new BadRequestObjectResult(new { message });
                owner = contact.ResponsibleUserId; linkedLegalId = contact.LegalEntityId;
                if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {sourceId} FOR SHARE NOWAIT", ct);
            }
            var owners = new HashSet<int>();
            if (!access.Visibility.SeesAll && owner.HasValue) owners.Add(owner.Value);
            var legalAccess = kind == "legalEntity" ? access : linkedLegalId.HasValue ? await ReadAccessAsync(db, actor, RolePermissionCatalog.LegalEntities, ct) : null;
            var legalId = kind == "legalEntity" ? sourceId : linkedLegalId;
            if (legalAccess is not null && legalId.HasValue)
            {
                legal = await db.LegalEntities.AsNoTracking().ApplyLegalEntityVisibility(legalAccess.Visibility).Where(x => x.Id == legalId.Value)
                    .Select(x => new LegalEntity { Id = x.Id, ShortName = x.ShortName, FullName = x.FullName, Phone = x.Phone, Email = x.Email, Director = x.Director, ResponsibleUserId = x.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (legal is not null)
                {
                    if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legal.Id} FOR SHARE NOWAIT", ct);
                    if (!legalAccess.Visibility.SeesAll && legal.ResponsibleUserId is int legalOwner) owners.Add(legalOwner);
                }
            }
            if (kind == "legalEntity" && legal is null) return new BadRequestObjectResult(new { message });
            if (client is not null)
            {
                client.LegalEntity = legal;
                // Do not fall back to a cached linked organization's name after access loss.
                if (linkedLegalId.HasValue && legal is null) client.Company = string.Empty;
            }
            if (contact is not null) contact.LegalEntity = legal;
            if (db.Database.IsNpgsql() && owners.Count > 0)
            {
                var ids = owners.Order().ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            return await write(clientCase, new(kind, client, contact, kind == "legalEntity" ? legal : null, caseAccess.Visibility, legalAccess?.Visibility));
        }, ct);

    private static async Task<SectionAccessContext?> ReadAccessAsync(PaymentContext db, ClaimsPrincipal actor, string section, CancellationToken ct)
    {
        var access = await db.ResolveSectionAccessAsync(actor, section, ct, requireApprovedUser: true);
        return access is not null && (access.Permission is not null || await db.HasApprovedUserSectionPermissionAsync(actor, section, p => p.CanView, ct)) ? access : null;
    }

    public static async Task<IActionResult> EditAsync(PaymentContext db, ClaimsPrincipal actor, int id,
        Func<ClientCase, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor.GetUserId();
        if (actor.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == actorId.Value && x.IsActive && x.IsApproved, ct)) return new UnauthorizedResult();
                return new ForbidResult();
            }
            if (access.Permission is null)
            {
                if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Cases, p => p.CanView && p.CanEdit, ct)) return new ForbidResult();
            }
            else if (!access.Permission.CanView || !access.Permission.CanEdit) return new ForbidResult();
            var clientCase = await db.ClientCases.ApplyClientCaseVisibility(access.Visibility).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (clientCase is null) return new NotFoundResult();
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll) await CaseEditTransaction.LockVisibleOwnersAsync(db, clientCase, ct);
            }
            var result = await write(clientCase, access);
            if (result is CreatedResult or OkObjectResult or NoContentResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Дело, связанное лицо или права доступа изменены либо сейчас заняты. Обновите данные перед повторной операцией." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}
