using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    public sealed class CasePositionReviewDto
    {
        public DateTime PositionReviewedAt { get; init; }
        public int? PositionReviewedByUserId { get; init; }
        public string PositionReviewedByName { get; init; } = string.Empty;
    }

    [HttpPost("{id:int}/position-review")]
    public async Task<ActionResult<CasePositionReviewDto>> ReviewCasePosition(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }
        if (IsFinishedCaseStatus(clientCase.Status))
        {
            return Conflict(new { message = "Позицию закрытого дела нельзя отмечать как проверенную." });
        }
        if (string.IsNullOrWhiteSpace(clientCase.Strategy))
        {
            return Conflict(new { message = "Сначала заполните позицию по делу и сохраните карточку." });
        }

        if (!clientCase.PositionReviewedAt.HasValue)
        {
            clientCase.PositionReviewedAt = DateTime.UtcNow;
            clientCase.PositionReviewedByUserId = User.GetUserId();
            clientCase.PositionReviewedByName = Truncate(User.GetDisplayName(), 256);
            AppendCaseEvent(clientCase, $"Позиция по делу проверена. Сотрудник: {clientCase.PositionReviewedByName}.");
            _db.SetAuditReason($"Проверена позиция по судебному делу #{id}.");
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new CasePositionReviewDto
        {
            PositionReviewedAt = clientCase.PositionReviewedAt.Value,
            PositionReviewedByUserId = clientCase.PositionReviewedByUserId,
            PositionReviewedByName = clientCase.PositionReviewedByName,
        });
    }
}
