using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    public sealed class NextHearingConfirmationDto
    {
        public DateTime NextHearingConfirmedForAt { get; init; }
        public DateTime NextHearingConfirmedAt { get; init; }
        public int? NextHearingConfirmedByUserId { get; init; }
        public string NextHearingConfirmedByName { get; init; } = string.Empty;
    }

    [HttpPost("{id:int}/next-hearing-confirmation")]
    public async Task<ActionResult<NextHearingConfirmationDto>> ConfirmNextHearing(int id, CancellationToken ct)
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
            return Conflict(new { message = "Дату заседания закрытого дела нельзя подтвердить." });
        }
        if (!clientCase.NextHearingAt.HasValue || clientCase.NextHearingAt.Value <= DateTime.UtcNow)
        {
            return Conflict(new { message = "Сначала укажите и сохраните будущую дату следующего заседания." });
        }

        if (clientCase.NextHearingConfirmedForAt != clientCase.NextHearingAt || !clientCase.NextHearingConfirmedAt.HasValue)
        {
            clientCase.NextHearingConfirmedForAt = clientCase.NextHearingAt;
            clientCase.NextHearingConfirmedAt = DateTime.UtcNow;
            clientCase.NextHearingConfirmedByUserId = User.GetUserId();
            clientCase.NextHearingConfirmedByName = Truncate(User.GetDisplayName(), 256);
            AppendCaseEvent(clientCase, $"Дата следующего заседания проверена: {clientCase.NextHearingAt:dd.MM.yyyy HH:mm}. Сотрудник: {clientCase.NextHearingConfirmedByName}.");
            _db.SetAuditReason($"Проверена дата следующего заседания по судебному делу #{id}.");
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new NextHearingConfirmationDto
        {
            NextHearingConfirmedForAt = clientCase.NextHearingConfirmedForAt!.Value,
            NextHearingConfirmedAt = clientCase.NextHearingConfirmedAt!.Value,
            NextHearingConfirmedByUserId = clientCase.NextHearingConfirmedByUserId,
            NextHearingConfirmedByName = clientCase.NextHearingConfirmedByName,
        });
    }

    private static void ResetNextHearingConfirmation(ClientCase clientCase)
    {
        clientCase.NextHearingConfirmedForAt = null;
        clientCase.NextHearingConfirmedAt = null;
        clientCase.NextHearingConfirmedByUserId = null;
        clientCase.NextHearingConfirmedByName = string.Empty;
    }
}
