using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private const int MaxActivePositionTemplatesPerScope = 200;

    public sealed record CasePositionTemplateDto(
        int Id,
        string Title,
        string Text,
        string Scope,
        DateTime UpdatedAt,
        bool CanManage);

    public sealed record CasePositionTemplateListDto(
        IReadOnlyList<CasePositionTemplateDto> Items,
        bool CanManageShared);

    public sealed record UpsertCasePositionTemplateRequest(string? Title, string? Text, string? Scope);

    [HttpGet("position-templates")]
    public async Task<ActionResult<CasePositionTemplateListDto>> GetPositionTemplates(CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();
        var canEditCases = (await ResolveCasesAccessAsync(ct))?.Permission?.CanEdit == true;
        var canManageShared = canEditCases && await _db.IsUserAdminAsync(userId.Value, ct);

        var templates = await _db.CasePositionTemplates.AsNoTracking()
            .Where(item => !item.IsArchived && (item.OwnerUserId == null || item.OwnerUserId == userId.Value))
            .OrderBy(item => item.OwnerUserId == null ? 0 : 1)
            .ThenBy(item => item.Title)
            .ThenBy(item => item.Id)
            .Select(item => new CasePositionTemplateDto(
                item.Id, item.Title, item.Text,
                item.OwnerUserId == null ? "shared" : "personal", item.UpdatedAt,
                canEditCases && (item.OwnerUserId == userId.Value || (item.OwnerUserId == null && canManageShared))))
            .ToListAsync(ct);

        return Ok(new CasePositionTemplateListDto(templates, canManageShared));
    }

    [HttpPost("position-templates")]
    public async Task<ActionResult<CasePositionTemplateDto>> CreatePositionTemplate(
        [FromBody] UpsertCasePositionTemplateRequest? request, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();
        if (!TryNormalizePositionTemplate(request, out var title, out var text, out var scope))
        {
            return BadRequest(new { message = "Укажите название до 160 символов, текст до 4000 символов и тип шаблона." });
        }
        if (scope == "shared" && !await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return Forbid();
        }

        int? ownerUserId = scope == "personal" ? userId.Value : null;
        var activeCount = await _db.CasePositionTemplates.CountAsync(
            item => !item.IsArchived && item.OwnerUserId == ownerUserId, ct);
        if (activeCount >= MaxActivePositionTemplatesPerScope)
        {
            return Conflict(new { message = "Достигнут предел активных шаблонов. Архивируйте ненужный шаблон." });
        }

        var now = DateTime.UtcNow;
        var template = new CasePositionTemplate
        {
            Title = title,
            Text = text,
            OwnerUserId = ownerUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.CasePositionTemplates.Add(template);
        _db.SetAuditReason($"Создан шаблон позиции по делу «{title}».");
        await _db.SaveChangesAsync(ct);
        return Created($"/api/v2/cases/position-templates/{template.Id}", MapPositionTemplate(template));
    }

    [HttpPut("position-templates/{templateId:int}")]
    public async Task<ActionResult<CasePositionTemplateDto>> UpdatePositionTemplate(
        int templateId, [FromBody] UpsertCasePositionTemplateRequest? request, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();
        if (!TryNormalizePositionTemplate(request, out var title, out var text, out var scope))
        {
            return BadRequest(new { message = "Укажите название до 160 символов и текст до 4000 символов." });
        }

        var template = await _db.CasePositionTemplates.FirstOrDefaultAsync(
            item => item.Id == templateId && !item.IsArchived, ct);
        if (template is null) return NotFound();
        if (scope != (template.OwnerUserId == null ? "shared" : "personal"))
        {
            return BadRequest(new { message = "Тип существующего шаблона менять нельзя. Создайте отдельную заготовку." });
        }
        if (!await CanManagePositionTemplateAsync(template, userId.Value, ct)) return Forbid();

        template.Title = title;
        template.Text = text;
        template.UpdatedAt = DateTime.UtcNow;
        _db.SetAuditReason($"Изменён шаблон позиции по делу #{template.Id}.");
        await _db.SaveChangesAsync(ct);
        return Ok(MapPositionTemplate(template));
    }

    [HttpDelete("position-templates/{templateId:int}")]
    public async Task<IActionResult> ArchivePositionTemplate(int templateId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();
        var template = await _db.CasePositionTemplates.FirstOrDefaultAsync(
            item => item.Id == templateId && !item.IsArchived, ct);
        if (template is null) return NotFound();
        if (!await CanManagePositionTemplateAsync(template, userId.Value, ct)) return Forbid();

        template.IsArchived = true;
        template.UpdatedAt = DateTime.UtcNow;
        _db.SetAuditReason($"Архивирован шаблон позиции по делу #{template.Id}.");
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> CanManagePositionTemplateAsync(CasePositionTemplate template, int userId, CancellationToken ct)
        => template.OwnerUserId == userId
            || (template.OwnerUserId == null && await _db.IsUserAdminAsync(userId, ct));

    private static bool TryNormalizePositionTemplate(
        UpsertCasePositionTemplateRequest? request, out string title, out string text, out string scope)
    {
        title = request?.Title?.Trim() ?? string.Empty;
        text = request?.Text?.Trim() ?? string.Empty;
        scope = request?.Scope?.Trim().ToLowerInvariant() ?? string.Empty;
        return title.Length is > 0 and <= 160
            && text.Length is > 0 and <= 4000
            && scope is "personal" or "shared";
    }

    private static CasePositionTemplateDto MapPositionTemplate(CasePositionTemplate template)
        => new(template.Id, template.Title, template.Text,
            template.OwnerUserId == null ? "shared" : "personal", template.UpdatedAt, true);
}
