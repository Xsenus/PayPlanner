using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/form-requirements")]
[Authorize]
public class FormRequirementsController : ControllerBase
{
    private readonly PaymentContext _db;

    public FormRequirementsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<FormFieldRequirementDto>>> Get([FromQuery] string? section, CancellationToken ct)
    {
        var query = _db.FormFieldRequirements.AsNoTracking().Where(x => x.IsRequired);
        if (!string.IsNullOrWhiteSpace(section))
        {
            query = query.Where(x => x.Section == section.Trim());
        }

        var data = await query
            .OrderBy(x => x.Section)
            .ThenBy(x => x.FieldKey)
            .Select(x => new FormFieldRequirementDto
            {
                Section = x.Section,
                FieldKey = x.FieldKey,
                IsRequired = x.IsRequired,
                UpdatedAt = x.UpdatedAt,
            })
            .ToListAsync(ct);

        return Ok(data);
    }

    [HttpPut]
    public async Task<ActionResult<FormFieldRequirementDto>> Upsert([FromBody] UpsertFormFieldRequirementRequest request, CancellationToken ct)
    {
        var section = request.Section?.Trim();
        var fieldKey = request.FieldKey?.Trim();
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(fieldKey))
        {
            return BadRequest("section и fieldKey обязательны");
        }

        var now = DateTime.UtcNow;
        var item = await _db.FormFieldRequirements.FirstOrDefaultAsync(x => x.Section == section && x.FieldKey == fieldKey, ct);
        if (item is null)
        {
            item = new FormFieldRequirement
            {
                Section = section,
                FieldKey = fieldKey,
                IsRequired = request.IsRequired,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.FormFieldRequirements.Add(item);
        }
        else
        {
            item.IsRequired = request.IsRequired;
            item.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new FormFieldRequirementDto
        {
            Section = item.Section,
            FieldKey = item.FieldKey,
            IsRequired = item.IsRequired,
            UpdatedAt = item.UpdatedAt,
        });
    }
}
