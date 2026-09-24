using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    public sealed class HearingPreparationDto
    {
        public DateTime HearingAt { get; init; }
        public DateTime? ContractCheckedAt { get; init; }
        public string ContractCheckedByName { get; init; } = string.Empty;
        public DateTime? AuthorityCheckedAt { get; init; }
        public string AuthorityCheckedByName { get; init; } = string.Empty;
        public DateTime? MaterialsCheckedAt { get; init; }
        public string MaterialsCheckedByName { get; init; } = string.Empty;
        public int[] DocumentIds { get; init; } = [];
    }

    public sealed class UpdateHearingPreparationRequest
    {
        public DateTime HearingAt { get; init; }
        public string CheckKind { get; init; } = string.Empty;
        public bool Checked { get; init; }
        public int[]? DocumentIds { get; init; }
    }

    [HttpGet("{id:int}/hearing-preparation")]
    public async Task<ActionResult<HearingPreparationDto>> GetHearingPreparation(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
            return forbidden;
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
            return MissingCasesVisibilityResult();

        var clientCase = await _db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(visibility)
            .Where(item => item.Id == id)
            .Select(item => new { item.NextHearingAt })
            .FirstOrDefaultAsync(ct);
        if (clientCase is null)
            return NotFound();
        if (!clientCase.NextHearingAt.HasValue)
            return Conflict(new { message = "Сначала назначьте дату заседания." });

        var preparation = await _db.ClientCaseHearingPreparations.AsNoTracking()
            .Include(item => item.Documents)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id && item.HearingAt == clientCase.NextHearingAt.Value, ct);
        return Ok(MapHearingPreparation(preparation, clientCase.NextHearingAt.Value));
    }

    [HttpPut("{id:int}/hearing-preparation")]
    public async Task<ActionResult<HearingPreparationDto>> UpdateHearingPreparation(
        int id, [FromBody] UpdateHearingPreparationRequest? request, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
            return forbidden;
        var checkKind = request?.CheckKind?.Trim().ToLowerInvariant();
        if (checkKind is not ("contract" or "authority" or "materials"))
            return BadRequest(new { message = "Выберите проверку договора, полномочий или материалов." });
        if (request!.DocumentIds is not null && checkKind != "materials")
            return BadRequest(new { message = "Документы можно привязать к проверке материалов." });
        if (request.DocumentIds is { Length: > 25 } || request.DocumentIds?.Any(item => item <= 0) == true)
            return BadRequest(new { message = "К заседанию можно привязать не более 25 документов дела." });

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
            return MissingCasesVisibilityResult();
        var clientCase = await _db.ClientCases.ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
            return NotFound();
        if (IsFinishedCaseStatus(clientCase.Status))
            return Conflict(new { message = "Подготовку по закрытому делу изменить нельзя." });
        if (!clientCase.NextHearingAt.HasValue || clientCase.NextHearingAt.Value <= DateTime.UtcNow)
            return Conflict(new { message = "Подготовку можно проверить только для предстоящего заседания." });
        if (request.HearingAt != clientCase.NextHearingAt.Value)
            return Conflict(new { message = "Дата заседания изменилась. Обновите карточку перед проверкой." });

        var preparation = await _db.ClientCaseHearingPreparations
            .Include(item => item.Documents)
            .FirstOrDefaultAsync(item => item.ClientCaseId == id && item.HearingAt == request.HearingAt, ct);
        var selectedIds = request.DocumentIds?.Distinct().Order().ToArray()
            ?? preparation?.Documents.Select(item => item.ClientCaseDocumentId).Order().ToArray()
            ?? [];
        var documentsChanged = request.DocumentIds is not null && preparation is not null
            && !selectedIds.SequenceEqual(preparation.Documents.Select(item => item.ClientCaseDocumentId).Order());
        if (checkKind == "materials" && request.Checked && selectedIds.Length == 0)
            return BadRequest(new { message = "Сначала привяжите хотя бы один подготовленный документ к заседанию." });
        if (request.DocumentIds is not null)
        {
            var found = await _db.ClientCaseDocuments.AsNoTracking()
                .CountAsync(item => item.ClientCaseId == id && selectedIds.Contains(item.Id), ct);
            if (found != selectedIds.Length)
                return BadRequest(new { message = "Один из документов не принадлежит этому делу." });
        }

        if (preparation is null)
        {
            preparation = new ClientCaseHearingPreparation { ClientCaseId = id, HearingAt = request.HearingAt };
            _db.ClientCaseHearingPreparations.Add(preparation);
        }
        var now = DateTime.UtcNow;
        var actorId = User.GetUserId();
        var actorName = Truncate(User.GetDisplayName(), 256);
        switch (checkKind)
        {
            case "contract":
                (preparation.ContractCheckedAt, preparation.ContractCheckedByUserId, preparation.ContractCheckedByName) =
                    ReviewValues(preparation.ContractCheckedAt, preparation.ContractCheckedByUserId,
                        preparation.ContractCheckedByName, request.Checked, now, actorId, actorName);
                break;
            case "authority":
                (preparation.AuthorityCheckedAt, preparation.AuthorityCheckedByUserId, preparation.AuthorityCheckedByName) =
                    ReviewValues(preparation.AuthorityCheckedAt, preparation.AuthorityCheckedByUserId,
                        preparation.AuthorityCheckedByName, request.Checked, now, actorId, actorName);
                break;
            case "materials":
                (preparation.MaterialsCheckedAt, preparation.MaterialsCheckedByUserId, preparation.MaterialsCheckedByName) =
                    ReviewValues(preparation.MaterialsCheckedAt, preparation.MaterialsCheckedByUserId,
                        preparation.MaterialsCheckedByName,
                        request.Checked && !(documentsChanged && preparation.MaterialsCheckedAt.HasValue),
                        now, actorId, actorName);
                if (request.DocumentIds is not null)
                {
                    foreach (var link in preparation.Documents.Where(item => !selectedIds.Contains(item.ClientCaseDocumentId)).ToArray())
                        preparation.Documents.Remove(link);
                    foreach (var documentId in selectedIds.Where(item => preparation.Documents.All(link => link.ClientCaseDocumentId != item)))
                        preparation.Documents.Add(new ClientCaseHearingPreparationDocument { ClientCaseDocumentId = documentId });
                }
                break;
        }
        preparation.UpdatedAt = now;
        _db.SetAuditReason($"Обновлена подготовка к заседанию по делу #{id}.");
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            or SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            return Conflict(new { message = "Подготовку изменил другой сотрудник. Обновите карточку и повторите проверку." });
        }
        return Ok(MapHearingPreparation(preparation, request.HearingAt));
    }

    private static (DateTime?, int?, string) ReviewValues(DateTime? checkedAt, int? checkedByUserId,
        string checkedByName, bool isChecked, DateTime now, int? actorId, string actorName)
    {
        if (isChecked && checkedAt.HasValue || !isChecked && !checkedAt.HasValue)
            return (checkedAt, checkedByUserId, checkedByName);
        return (isChecked ? now : null, isChecked ? actorId : null, isChecked ? actorName : string.Empty);
    }

    private static HearingPreparationDto MapHearingPreparation(ClientCaseHearingPreparation? item, DateTime hearingAt) => new()
    {
        HearingAt = hearingAt,
        ContractCheckedAt = item?.ContractCheckedAt,
        ContractCheckedByName = item?.ContractCheckedByName ?? string.Empty,
        AuthorityCheckedAt = item?.AuthorityCheckedAt,
        AuthorityCheckedByName = item?.AuthorityCheckedByName ?? string.Empty,
        MaterialsCheckedAt = item?.MaterialsCheckedAt,
        MaterialsCheckedByName = item?.MaterialsCheckedByName ?? string.Empty,
        DocumentIds = item?.Documents.Select(link => link.ClientCaseDocumentId).Order().ToArray() ?? [],
    };

    private async Task<HashSet<(int CaseId, DateTime HearingAt)>> LoadReviewedHearingMaterialsAsync(
        IReadOnlyCollection<(int CaseId, DateTime HearingAt)> hearings, CancellationToken ct)
    {
        if (hearings.Count == 0)
            return [];
        var caseIds = hearings.Select(item => item.CaseId).Distinct().ToArray();
        var earliest = hearings.Min(item => item.HearingAt);
        var latest = hearings.Max(item => item.HearingAt);
        var reviewed = await _db.ClientCaseHearingPreparations.AsNoTracking()
            .Where(item => caseIds.Contains(item.ClientCaseId)
                && item.HearingAt >= earliest && item.HearingAt <= latest
                && item.MaterialsCheckedAt.HasValue && item.Documents.Any())
            .Select(item => new { item.ClientCaseId, item.HearingAt })
            .ToListAsync(ct);
        return reviewed.Select(item => (item.ClientCaseId, item.HearingAt)).ToHashSet();
    }
}
