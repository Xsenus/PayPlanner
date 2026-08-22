using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/object-audit")]
[Authorize]
public class ObjectAuditController : ControllerBase
{
    private static readonly string[] CaseScopedAuditObjectTypes =
    [
        "client-case",
        "client-case-court-event",
        "client-case-court-party",
        "client-case-document",
        "client-case-procedural-deadline",
        "client-case-related-party",
        "client-case-work-order",
        "client-case-work-order-item",
    ];

    private readonly PaymentContext _db;

    public ObjectAuditController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ObjectAuditEventDto>>> GetEvents(
        [FromQuery] string? search,
        [FromQuery] string? objectType,
        [FromQuery] string? action,
        [FromQuery] string? actor,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string[]? searchAliases = null,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtcExclusive = null,
        CancellationToken ct = default)
    {
        var access = await GetDeletedObjectsAccessAsync(ct);
        if (!access.CanView)
        {
            return Forbid();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = _db.ObjectAuditEvents
            .AsNoTracking()
            .AsQueryable();

        query = await ApplyObjectAuditVisibilityAsync(query, access, ct);

        if (!string.IsNullOrWhiteSpace(objectType))
        {
            var normalizedObjectType = NormalizeObjectType(objectType);
            query = query.Where(item => item.ObjectType == normalizedObjectType);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalizedAction = action.Trim().ToLowerInvariant();
            query = query.Where(item => item.Action.ToLower() == normalizedAction);
        }

        if (fromUtc.HasValue)
        {
            var boundary = fromUtc.Value.UtcDateTime;
            query = query.Where(item => item.CreatedAt >= boundary);
        }
        else if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(item => item.CreatedAt >= fromDate);
        }

        if (toUtcExclusive.HasValue)
        {
            var boundary = toUtcExclusive.Value.UtcDateTime;
            query = query.Where(item => item.CreatedAt < boundary);
        }
        else if (to.HasValue)
        {
            var toExclusive = to.Value.Date.AddDays(1);
            query = query.Where(item => item.CreatedAt < toExclusive);
        }

        var actorFilter = actor?.Trim();
        var searchFilter = search?.Trim();
        var normalizedSearchAliases = (searchAliases ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => item.Length <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        var supportsServerSideCaseInsensitiveSearch = _db.Database.IsNpgsql();

        if (supportsServerSideCaseInsensitiveSearch && !string.IsNullOrWhiteSpace(actorFilter))
        {
            var actorPattern = $"%{EscapeLikePattern(actorFilter)}%";
            query = query.Where(item => EF.Functions.ILike(item.ActorUserName ?? string.Empty, actorPattern, "\\"));
        }

        if (supportsServerSideCaseInsensitiveSearch && !string.IsNullOrWhiteSpace(searchFilter))
        {
            var searchPattern = $"%{EscapeLikePattern(searchFilter)}%";
            var aliasPattern = normalizedSearchAliases.Length > 0
                ? string.Join('|', normalizedSearchAliases.Select(Regex.Escape))
                : null;
            var parsedObjectId = int.TryParse(searchFilter, out var objectId) ? objectId : (int?)null;
            query = query.Where(item =>
                EF.Functions.ILike(item.ObjectType, searchPattern, "\\")
                || EF.Functions.ILike(item.Description ?? string.Empty, searchPattern, "\\")
                || EF.Functions.ILike(item.MetadataJson ?? string.Empty, searchPattern, "\\")
                || EF.Functions.ILike(item.ActorUserName ?? string.Empty, searchPattern, "\\")
                || (aliasPattern != null && Regex.IsMatch(item.MetadataJson ?? string.Empty, aliasPattern, RegexOptions.IgnoreCase))
                || (parsedObjectId.HasValue && item.ObjectId == parsedObjectId.Value));
        }

        if (!supportsServerSideCaseInsensitiveSearch
            && (!string.IsNullOrWhiteSpace(actorFilter) || !string.IsNullOrWhiteSpace(searchFilter)))
        {
            var filteredItems = (await query
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id)
                    .ToListAsync(ct))
                .Where(item => MatchesActorFilter(item, actorFilter))
                .Where(item => MatchesSearchFilter(item, searchFilter, normalizedSearchAliases))
                .ToList();

            return Ok(new PagedResponse<ObjectAuditEventDto>
            {
                Items = filteredItems
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapAuditEvent)
                    .ToList(),
                Total = filteredItems.Count,
                Page = page,
                PageSize = pageSize,
            });
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new ObjectAuditEventDto
            {
                Id = item.Id,
                ObjectType = item.ObjectType,
                ObjectId = item.ObjectId,
                Action = item.Action,
                Description = item.Description,
                ActorUserId = item.ActorUserId,
                ActorUserName = item.ActorUserName,
                MetadataJson = item.MetadataJson,
                CreatedAt = item.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(new PagedResponse<ObjectAuditEventDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{objectType}/{objectId:int}")]
    public async Task<ActionResult<IReadOnlyCollection<ObjectAuditEventDto>>> GetHistory(
        string objectType,
        int objectId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            return BadRequest("Укажите тип объекта.");
        }

        var normalizedObjectType = NormalizeObjectType(objectType);
        var access = await GetDeletedObjectsAccessAsync(ct);
        if (!access.CanView)
        {
            return Forbid();
        }

        if (!await CanViewAuditObjectAsync(normalizedObjectType, objectId, access, ct))
        {
            return Forbid();
        }

        var history = await _db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == normalizedObjectType && item.ObjectId == objectId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(100)
            .Select(item => new ObjectAuditEventDto
            {
                Id = item.Id,
                ObjectType = item.ObjectType,
                ObjectId = item.ObjectId,
                Action = item.Action,
                Description = item.Description,
                ActorUserId = item.ActorUserId,
                ActorUserName = item.ActorUserName,
                MetadataJson = item.MetadataJson,
                CreatedAt = item.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(history);
    }

    [HttpGet("deleted/{objectType}")]
    public async Task<ActionResult<IReadOnlyCollection<DeletedObjectAuditItemDto>>> GetDeletedObjects(
        string objectType,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var access = await GetDeletedObjectsAccessAsync(ct);
        if (!access.CanView)
        {
            return Forbid();
        }

        var normalizedObjectType = NormalizeObjectType(objectType);

        var items = normalizedObjectType switch
        {
            "client" => await QueryDeletedClientsAsync(search, ct),
            "client-case" => await QueryDeletedClientCasesAsync(search, ct),
            "payment" => await QueryDeletedPaymentsAsync(search, ct),
            "act" => await QueryDeletedActsAsync(search, ct),
            "contract" => await QueryDeletedContractsAsync(search, ct),
            "legal-entity" => await QueryDeletedLegalEntitiesAsync(search, ct),
            "contact" => await QueryDeletedContactsAsync(search, ct),
            _ => null,
        };

        if (items is null)
        {
            return BadRequest("Неподдерживаемый тип объекта.");
        }

        return Ok(items);
    }

    [HttpPost("{objectType}/{objectId:int}/restore")]
    public async Task<IActionResult> RestoreObject(
        string objectType,
        int objectId,
        CancellationToken ct)
    {
        var access = await GetDeletedObjectsAccessAsync(ct);
        if (!access.CanRestore)
        {
            return Forbid();
        }

        var normalizedObjectType = NormalizeObjectType(objectType);

        var restored = normalizedObjectType switch
        {
            "client" => await RestoreClientAsync(objectId, ct),
            "client-case" => await RestoreClientCaseAsync(objectId, ct),
            "payment" => await RestorePaymentAggregateAsync(objectId, ct),
            "act" => await RestoreActAsync(objectId, ct),
            "contract" => await RestoreContractAsync(objectId, ct),
            "legal-entity" => await RestoreLegalEntityAsync(objectId, ct),
            "contact" => await RestoreContactAsync(objectId, ct),
            _ => null,
        };

        if (restored is null)
        {
            return BadRequest("Неподдерживаемый тип объекта.");
        }

        if (restored is false)
        {
            return NotFound();
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<DeletedObjectsAccessSnapshot> GetDeletedObjectsAccessAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return DeletedObjectsAccessSnapshot.None;
        }

        if (await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return DeletedObjectsAccessSnapshot.Admin;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Audit, ct);
        if (permission is null)
        {
            return DeletedObjectsAccessSnapshot.None;
        }

        return new DeletedObjectsAccessSnapshot(
            permission.CanView,
            permission.CanEdit,
            IsAdmin: false);
    }

    private async Task<IQueryable<ObjectAuditEvent>> ApplyObjectAuditVisibilityAsync(
        IQueryable<ObjectAuditEvent> query,
        DeletedObjectsAccessSnapshot access,
        CancellationToken ct)
    {
        if (access.IsAdmin)
        {
            return query;
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (casesVisibility is null)
        {
            return query.Where(item => !CaseScopedAuditObjectTypes.Contains(item.ObjectType));
        }

        var visibleCases = _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(casesVisibility);

        return query.Where(item =>
            !CaseScopedAuditObjectTypes.Contains(item.ObjectType)
            || (item.ObjectType == "client-case" && visibleCases.Any(clientCase => clientCase.Id == item.ObjectId))
            || (item.ObjectType == "client-case-court-event" && _db.ClientCaseCourtEvents.Any(courtEvent =>
                courtEvent.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == courtEvent.ClientCaseId)))
            || (item.ObjectType == "client-case-court-party" && _db.ClientCaseCourtParties.Any(courtParty =>
                courtParty.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == courtParty.ClientCaseId)))
            || (item.ObjectType == "client-case-document" && _db.ClientCaseDocuments.Any(document =>
                document.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == document.ClientCaseId)))
            || (item.ObjectType == "client-case-procedural-deadline" && _db.ClientCaseProceduralDeadlines.Any(deadline =>
                deadline.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == deadline.ClientCaseId)))
            || (item.ObjectType == "client-case-related-party" && _db.ClientCaseRelatedParties.Any(party =>
                party.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == party.ClientCaseId)))
            || (item.ObjectType == "client-case-work-order" && _db.ClientCaseWorkOrders.Any(workOrder =>
                workOrder.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == workOrder.ClientCaseId)))
            || (item.ObjectType == "client-case-work-order-item" && _db.ClientCaseWorkOrderItems.Any(workOrderItem =>
                workOrderItem.Id == item.ObjectId
                && visibleCases.Any(clientCase => clientCase.Id == workOrderItem.WorkOrder.ClientCaseId))));
    }

    private async Task<bool> CanViewAuditObjectAsync(
        string objectType,
        int objectId,
        DeletedObjectsAccessSnapshot access,
        CancellationToken ct)
    {
        if (access.IsAdmin || !CaseScopedAuditObjectTypes.Contains(objectType))
        {
            return true;
        }

        var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (casesVisibility is null)
        {
            return false;
        }

        var visibleCases = _db.ClientCases
            .AsNoTracking()
            .ApplyClientCaseVisibility(casesVisibility);

        return objectType switch
        {
            "client-case" => await visibleCases.AnyAsync(clientCase => clientCase.Id == objectId, ct),
            "client-case-court-event" => await _db.ClientCaseCourtEvents
                .AsNoTracking()
                .AnyAsync(courtEvent => courtEvent.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == courtEvent.ClientCaseId), ct),
            "client-case-court-party" => await _db.ClientCaseCourtParties
                .AsNoTracking()
                .AnyAsync(courtParty => courtParty.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == courtParty.ClientCaseId), ct),
            "client-case-document" => await _db.ClientCaseDocuments
                .AsNoTracking()
                .AnyAsync(document => document.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == document.ClientCaseId), ct),
            "client-case-procedural-deadline" => await _db.ClientCaseProceduralDeadlines
                .AsNoTracking()
                .AnyAsync(deadline => deadline.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == deadline.ClientCaseId), ct),
            "client-case-related-party" => await _db.ClientCaseRelatedParties
                .AsNoTracking()
                .AnyAsync(party => party.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == party.ClientCaseId), ct),
            "client-case-work-order" => await _db.ClientCaseWorkOrders
                .AsNoTracking()
                .AnyAsync(workOrder => workOrder.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == workOrder.ClientCaseId), ct),
            "client-case-work-order-item" => await _db.ClientCaseWorkOrderItems
                .AsNoTracking()
                .AnyAsync(workOrderItem => workOrderItem.Id == objectId
                    && visibleCases.Any(clientCase => clientCase.Id == workOrderItem.WorkOrder.ClientCaseId), ct),
            _ => true,
        };
    }

    private static string NormalizeObjectType(string objectType)
        => objectType.Trim().ToLowerInvariant();

    private static string EscapeLikePattern(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static ObjectAuditEventDto MapAuditEvent(ObjectAuditEvent item)
        => new()
        {
            Id = item.Id,
            ObjectType = item.ObjectType,
            ObjectId = item.ObjectId,
            Action = item.Action,
            Description = item.Description,
            ActorUserId = item.ActorUserId,
            ActorUserName = item.ActorUserName,
            MetadataJson = item.MetadataJson,
            CreatedAt = item.CreatedAt,
        };

    private static bool MatchesActorFilter(ObjectAuditEvent item, string? actorFilter)
    {
        if (string.IsNullOrWhiteSpace(actorFilter))
        {
            return true;
        }

        return (item.ActorUserName ?? string.Empty)
            .Contains(actorFilter.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesSearchFilter(
        ObjectAuditEvent item,
        string? searchFilter,
        IReadOnlyCollection<string> searchAliases)
    {
        if (string.IsNullOrWhiteSpace(searchFilter))
        {
            return true;
        }

        var term = searchFilter.Trim();
        var parsedObjectId = int.TryParse(term, out var objectId) ? objectId : (int?)null;

        return item.ObjectType.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (item.Description?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (item.MetadataJson?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || searchAliases.Any(alias => item.MetadataJson?.Contains(alias, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.ActorUserName?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (parsedObjectId.HasValue && item.ObjectId == parsedObjectId.Value);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedClientsAsync(string? search, CancellationToken ct)
    {
        var query = _db.Clients
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.Name.ToLower().Contains(term)
                || entity.Phone.ToLower().Contains(term)
                || entity.Email.ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenBy(entity => entity.Name)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "client",
                ObjectId = entity.Id,
                DisplayName = entity.Name,
                Subtitle = string.IsNullOrWhiteSpace(entity.Phone) ? entity.Email : entity.Phone,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedClientCasesAsync(string? search, CancellationToken ct)
    {
        var query = _db.ClientCases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.Title.ToLower().Contains(term)
                || entity.Description.ToLower().Contains(term)
                || entity.Client != null && entity.Client.Name.ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenBy(entity => entity.Title)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "client-case",
                ObjectId = entity.Id,
                DisplayName = entity.Title,
                Subtitle = entity.Client != null ? entity.Client.Name : null,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedPaymentsAsync(string? search, CancellationToken ct)
    {
        var query = _db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.Description.ToLower().Contains(term)
                || (entity.CounterpartyName ?? string.Empty).ToLower().Contains(term)
                || (entity.Account ?? string.Empty).ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenByDescending(entity => entity.Date)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "payment",
                ObjectId = entity.Id,
                DisplayName = entity.DocumentKind == DocumentKind.Invoice
                    ? $"Счет №{(string.IsNullOrWhiteSpace(entity.Account) ? entity.Id.ToString() : entity.Account)}"
                    : (string.IsNullOrWhiteSpace(entity.Description) ? $"Платеж #{entity.Id}" : entity.Description),
                Subtitle = entity.CounterpartyName,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedActsAsync(string? search, CancellationToken ct)
    {
        var query = _db.Acts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.Number.ToLower().Contains(term)
                || (entity.Title ?? string.Empty).ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenByDescending(entity => entity.Date)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "act",
                ObjectId = entity.Id,
                DisplayName = $"Акт №{entity.Number}",
                Subtitle = entity.Title,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedContractsAsync(string? search, CancellationToken ct)
    {
        var query = _db.Contracts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.Number.ToLower().Contains(term)
                || (entity.Title ?? string.Empty).ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenByDescending(entity => entity.Date)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "contract",
                ObjectId = entity.Id,
                DisplayName = $"Договор №{entity.Number}",
                Subtitle = entity.Title,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedLegalEntitiesAsync(string? search, CancellationToken ct)
    {
        var query = _db.LegalEntities
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.ShortName.ToLower().Contains(term)
                || (entity.Inn ?? string.Empty).ToLower().Contains(term)
                || (entity.Email ?? string.Empty).ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenBy(entity => entity.ShortName)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "legal-entity",
                ObjectId = entity.Id,
                DisplayName = entity.ShortName,
                Subtitle = entity.Inn,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<List<DeletedObjectAuditItemDto>> QueryDeletedContactsAsync(string? search, CancellationToken ct)
    {
        var query = _db.ExternalContacts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => EF.Property<bool>(entity, "IsDeleted"));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(entity =>
                entity.FullName.ToLower().Contains(term)
                || (entity.Phone ?? string.Empty).ToLower().Contains(term)
                || (entity.Email ?? string.Empty).ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(entity => EF.Property<DateTime?>(entity, "DeletedAt"))
            .ThenBy(entity => entity.FullName)
            .Select(entity => new DeletedObjectAuditItemDto
            {
                ObjectType = "contact",
                ObjectId = entity.Id,
                DisplayName = entity.FullName,
                Subtitle = entity.LegalEntity != null ? entity.LegalEntity.ShortName : entity.Phone,
                CreatedAt = entity.CreatedAt,
                CreatedByUserName = EF.Property<string?>(entity, "CreatedByUserName"),
                UpdatedAt = EF.Property<DateTime?>(entity, "UpdatedAt"),
                UpdatedByUserName = EF.Property<string?>(entity, "UpdatedByUserName"),
                DeletedAt = EF.Property<DateTime?>(entity, "DeletedAt"),
                DeletedByUserName = EF.Property<string?>(entity, "DeletedByUserName"),
            })
            .Take(100)
            .ToListAsync(ct);
    }

    private async Task<bool?> RestoreClientAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private async Task<bool?> RestoreClientCaseAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.ClientCases
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private async Task<bool?> RestorePaymentAggregateAsync(int objectId, CancellationToken ct)
    {
        var target = await _db.Payments
            .IgnoreQueryFilters()
            .Include(item => item.Invoice)
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        if (target is null)
        {
            return false;
        }

        var restoredAnything = RestoreEntity(target);

        if (target.DocumentKind == DocumentKind.Invoice && target.InvoiceId.HasValue)
        {
            var relatedPayments = await _db.Payments
                .IgnoreQueryFilters()
                .Where(item => item.InvoiceId == target.InvoiceId.Value)
                .ToListAsync(ct);

            foreach (var payment in relatedPayments)
            {
                restoredAnything = RestoreEntity(payment) || restoredAnything;
            }

            var invoice = await _db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == target.InvoiceId.Value, ct);
            if (invoice is not null)
            {
                restoredAnything = RestoreEntity(invoice) || restoredAnything;
            }

            var paymentIds = relatedPayments.Select(item => item.Id).ToList();
            if (paymentIds.Count > 0)
            {
                var lineItems = await _db.InvoiceLineItems
                    .IgnoreQueryFilters()
                    .Where(item => paymentIds.Contains(item.PaymentId))
                    .ToListAsync(ct);

                foreach (var lineItem in lineItems)
                {
                    restoredAnything = RestoreEntity(lineItem) || restoredAnything;
                }
            }
        }

        return restoredAnything;
    }

    private async Task<bool?> RestoreActAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.Acts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private async Task<bool?> RestoreContractAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.Contracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private async Task<bool?> RestoreLegalEntityAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private async Task<bool?> RestoreContactAsync(int objectId, CancellationToken ct)
    {
        var entity = await _db.ExternalContacts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == objectId, ct);

        return entity is null ? false : RestoreEntity(entity);
    }

    private bool RestoreEntity(object entity)
    {
        var entry = _db.Entry(entity);
        if (entry.Metadata.FindProperty("IsDeleted") is null)
        {
            return false;
        }

        if (!(entry.Property("IsDeleted").CurrentValue as bool? ?? false))
        {
            return false;
        }

        entry.Property("IsDeleted").CurrentValue = false;

        if (entry.Metadata.FindProperty("DeletedAt") is not null)
        {
            entry.Property("DeletedAt").CurrentValue = null;
        }

        if (entry.Metadata.FindProperty("DeletedByUserId") is not null)
        {
            entry.Property("DeletedByUserId").CurrentValue = null;
        }

        if (entry.Metadata.FindProperty("DeletedByUserName") is not null)
        {
            entry.Property("DeletedByUserName").CurrentValue = null;
        }

        entry.State = EntityState.Modified;
        return true;
    }

    private readonly record struct DeletedObjectsAccessSnapshot(bool CanView, bool CanRestore, bool IsAdmin)
    {
        public static DeletedObjectsAccessSnapshot None { get; } = new(false, false, false);
        public static DeletedObjectsAccessSnapshot Admin { get; } = new(true, true, true);
    }
}
