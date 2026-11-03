using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private enum MergeAction { View, Edit, Merge, CreateMerge }
    private sealed record MergeAliasSource(int Id, int? ResponsibleUserId, int? LegalEntityId, int? ParentContactId, int? LinkedClientId, int? LinkedLegalEntityId);

    private async Task<ActionResult<ContactMergePreview>> RunMergeBoundaryAsync(
        Func<Task<ActionResult<ContactMergePreview>>> action, bool execute, CancellationToken ct)
    {
        var actor = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || actor is not > 0) return Unauthorized();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(_db, actor.Value, ct);
            if (!await _db.Users.AsNoTracking().AnyAsync(x => x.Id == actor.Value && x.IsActive && x.IsApproved, ct)) return Unauthorized();
            var result = await action();
            if (execute && result.Result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Карточки контактов или права доступа изменены либо сейчас заняты. Обновите предпросмотр перед повторным объединением." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static bool AllowsMergeAction(RolePermissionValues p, MergeAction action)
        => p.CanView && (action == MergeAction.View || p.CanEdit)
            && (action is not (MergeAction.Merge or MergeAction.CreateMerge) || p.CanDelete)
            && (action != MergeAction.CreateMerge || p.CanCreate);

    private async Task<SectionAccessContext?> MergeSectionAsync(string section, MergeAction action, CancellationToken ct)
    {
        var access = await _db.ResolveSectionAccessAsync(User, section, ct, requireApprovedUser: true);
        if (access is null) return null;
        if (access.Permission is not null) return AllowsMergeAction(access.Permission, action) ? access : null;
        if (_db.Database.IsNpgsql())
        {
            var actor = User.GetUserId();
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actor}) FOR UPDATE NOWAIT", ct);
        }
        return await _db.HasApprovedUserSectionPermissionAsync(User, section, p => AllowsMergeAction(p, action), ct) ? access : null;
    }

    private async Task LockMergeRecordsAsync(string kind, IEnumerable<int> sourceIds, bool execute, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql()) return;
        var ids = sourceIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return;
        if (kind == "client")
        {
            if (execute) await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            else await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
        else if (kind == "external")
        {
            if (execute) await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            else await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
        else
        {
            if (execute) await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            else await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
    }

    private async Task<(MergePair? Pair, ActionResult? Error)> AdmitMergePairAsync(MergePair pair,
        SectionAccessContext? clients, SectionAccessContext? legal, bool execute, CancellationToken ct)
    {
        var sources = new[] { pair.Primary, pair.Duplicate };
        var sourceClients = sources.OfType<Client>().ToArray(); var sourceLegal = sources.OfType<LegalEntity>().ToArray(); var sourceContacts = sources.OfType<ExternalContact>().ToArray();
        await LockMergeRecordsAsync("client", sourceClients.Select(x => x.Id), execute, ct);
        await LockMergeRecordsAsync("external", sourceContacts.Select(x => x.Id), execute, ct);
        await LockMergeRecordsAsync("legal", sourceLegal.Select(x => x.Id), execute, ct);
        var owners = new HashSet<int>();
        void Owner(int? id, SectionAccessContext? access) { if (access is { Visibility.SeesAll: false } && id.HasValue) owners.Add(id.Value); }
        foreach (var x in sourceClients) Owner(x.ResponsibleUserId, clients);
        foreach (var x in sourceLegal) Owner(x.ResponsibleUserId, legal);
        foreach (var x in sourceContacts) Owner(x.ResponsibleUserId, clients);

        // Aliases are additional records: the selected full profile's scope does not authorize them.
        var clientIds = sourceClients.Select(x => x.Id).ToArray(); var legalIds = sourceLegal.Select(x => x.Id).ToArray();
        MergeAliasSource[] aliases = clientIds.Length == 0 && legalIds.Length == 0 ? [] : await _db.ExternalContacts.AsNoTracking().Where(x => x.LinkedClientId.HasValue && clientIds.Contains(x.LinkedClientId.Value)
                || x.LinkedLegalEntityId.HasValue && legalIds.Contains(x.LinkedLegalEntityId.Value))
            .OrderBy(x => x.Id).Take(3).Select(x => new MergeAliasSource(x.Id, x.ResponsibleUserId, x.LegalEntityId, x.ParentContactId, x.LinkedClientId, x.LinkedLegalEntityId)).ToArrayAsync(ct);
        if (aliases.Length > 0)
        {
            clients ??= await MergeSectionAsync(RolePermissionCatalog.Clients, MergeAction.View, ct);
            if (clients is null || aliases.Any(x => !clients.Visibility.SeesAll && (!x.ResponsibleUserId.HasValue || !clients.Visibility.VisibleUserIds.Contains(x.ResponsibleUserId.Value))))
                return (null, BadRequest(new { message = "Связанный контакт не найден или недоступен для объединения." }));
            var duplicateId = GetMergeId(pair.Duplicate);
            var changesAlias = pair.Duplicate is ExternalContact || pair.Duplicate is Client && aliases.Any(x => x.LinkedClientId == duplicateId)
                || pair.Duplicate is LegalEntity && aliases.Any(x => x.LinkedLegalEntityId == duplicateId);
            if (changesAlias && (clients.Permission is { } p ? !AllowsMergeAction(p, MergeAction.Edit)
                : !await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, permission => AllowsMergeAction(permission, MergeAction.Edit), ct))) return (null, Forbid());
            await LockMergeRecordsAsync("external", aliases.Select(x => x.Id), execute && changesAlias, ct);
            foreach (var x in aliases) Owner(x.ResponsibleUserId, clients);
        }
        var linkedClientIds = sourceContacts.Where(x => x.LinkedClientId.HasValue).Select(x => x.LinkedClientId!.Value)
            .Concat(aliases.Where(x => x.LinkedClientId.HasValue).Select(x => x.LinkedClientId!.Value)).Except(clientIds).Distinct().ToArray();
        if (linkedClientIds.Length > 0)
        {
            clients ??= await MergeSectionAsync(RolePermissionCatalog.Clients, MergeAction.View, ct);
            if (clients is null) return (null, BadRequest(new { message = "Связанный клиент не найден или недоступен для объединения." }));
            var linked = await _db.Clients.AsNoTracking().ApplyClientVisibility(clients.Visibility).Where(x => linkedClientIds.Contains(x.Id)).Select(x => new { x.Id, x.ResponsibleUserId }).ToArrayAsync(ct);
            if (linked.Length != linkedClientIds.Length) return (null, BadRequest(new { message = "Связанный клиент не найден или недоступен для объединения." }));
            await LockMergeRecordsAsync("client", linkedClientIds, false, ct); foreach (var x in linked) Owner(x.ResponsibleUserId, clients);
        }
        var linkedLegalIds = sourceClients.Where(x => x.LegalEntityId.HasValue).Select(x => x.LegalEntityId!.Value)
            .Concat(sourceContacts.Where(x => x.LegalEntityId.HasValue).Select(x => x.LegalEntityId!.Value))
            .Concat(sourceContacts.Where(x => x.LinkedLegalEntityId.HasValue).Select(x => x.LinkedLegalEntityId!.Value))
            .Concat(aliases.Where(x => x.LinkedLegalEntityId.HasValue).Select(x => x.LinkedLegalEntityId!.Value))
            .Concat(aliases.Where(x => x.LegalEntityId.HasValue).Select(x => x.LegalEntityId!.Value)).Except(legalIds).Distinct().ToArray();
        if (linkedLegalIds.Length > 0)
        {
            legal ??= await MergeSectionAsync(RolePermissionCatalog.LegalEntities, MergeAction.View, ct);
            if (legal is null) return (null, BadRequest(new { message = "Связанная организация не найдена или недоступна для объединения." }));
            var linked = await _db.LegalEntities.AsNoTracking().ApplyLegalEntityVisibility(legal.Visibility).Where(x => linkedLegalIds.Contains(x.Id)).Select(x => new { x.Id, x.ResponsibleUserId }).ToArrayAsync(ct);
            if (linked.Length != linkedLegalIds.Length) return (null, BadRequest(new { message = "Связанная организация не найдена или недоступна для объединения." }));
            await LockMergeRecordsAsync("legal", linkedLegalIds, false, ct); foreach (var x in linked) Owner(x.ResponsibleUserId, legal);
        }
        var sourceContactIds = sourceContacts.Select(x => x.Id).ToArray();
        var parentIds = sourceContacts.Where(x => x.ParentContactId.HasValue).Select(x => x.ParentContactId!.Value)
            .Concat(aliases.Where(x => x.ParentContactId.HasValue).Select(x => x.ParentContactId!.Value)).Except(sourceContactIds).Distinct().ToArray();
        if (parentIds.Length > 0)
        {
            clients ??= await MergeSectionAsync(RolePermissionCatalog.Clients, MergeAction.View, ct);
            if (clients is null) return (null, BadRequest(new { message = "Родительский контакт не найден или недоступен для объединения." }));
            var parents = await BuildVisibleExternalContactsQuery(clients.Visibility).AsNoTracking().Where(x => parentIds.Contains(x.Id)).Select(x => new { x.Id, x.ResponsibleUserId }).ToArrayAsync(ct);
            if (parents.Length != parentIds.Length) return (null, BadRequest(new { message = "Родительский контакт не найден или недоступен для объединения." }));
            await LockMergeRecordsAsync("external", parentIds, false, ct); foreach (var x in parents) Owner(x.ResponsibleUserId, clients);
        }
        if (_db.Database.IsNpgsql() && owners.Count > 0)
        {
            var ids = owners.Order().ToArray();
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
        return (pair, null);
    }
}
