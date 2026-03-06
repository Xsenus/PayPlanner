using System.Reflection;
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

    private static readonly (string Key, string Title, Type ModelType)[] SectionModels =
    {
        ("clients", "Клиенты", typeof(Client)),
        ("legal-entities", "Юридические лица", typeof(LegalEntityRequest)),
        ("payments", "Платежи", typeof(Payment)),
        ("contract-invoices", "Счета договора", typeof(UpsertInvoiceRequest)),
        ("acts", "Акты", typeof(UpsertActRequest)),
        ("contracts", "Договоры", typeof(UpsertContractRequest)),
    };

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

    [HttpGet("sections")]
    public ActionResult<List<FormRequirementSectionMetadataDto>> GetSections()
    {
        var data = SectionModels.Select(section => new FormRequirementSectionMetadataDto
        {
            Key = section.Key,
            Title = section.Title,
            Fields = section.ModelType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && IsSupportedFieldType(p.PropertyType) && !IsExcludedProperty(p.Name))
                .OrderBy(p => p.Name)
                .Select(p => new FormRequirementFieldMetadataDto
                {
                    Key = p.Name,
                    Label = HumanizePropertyName(p.Name),
                })
                .ToList(),
        }).ToList();

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

    private static bool IsExcludedProperty(string name)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Id", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy"
        };
        return excluded.Contains(name);
    }

    private static bool IsSupportedFieldType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum) return true;
        if (underlying == typeof(string)) return true;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return true;
        if (underlying == typeof(long)) return true;
        if (underlying == typeof(short)) return true;
        if (underlying == typeof(decimal)) return true;
        if (underlying == typeof(double)) return true;
        if (underlying == typeof(float)) return true;
        if (underlying == typeof(DateTime)) return true;
        if (underlying == typeof(Guid)) return true;
        return false;
    }

    private static string HumanizePropertyName(string key)
    {
        var acronyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Inn"] = "ИНН",
            ["Kpp"] = "КПП",
            ["Ogrn"] = "ОГРН",
            ["Oktmo"] = "ОКТМО",
            ["Okato"] = "ОКАТО",
            ["Okpo"] = "ОКПО",
            ["Okogu"] = "ОКОГУ",
            ["Okfs"] = "ОКФС",
            ["Okopf"] = "ОКОПФ",
            ["Okved"] = "ОКВЭД",
            ["Pfr"] = "ПФР",
            ["Fss"] = "ФСС",
            ["Vat"] = "НДС",
            ["Ifns"] = "ИФНС",
            ["Kbk"] = "КБК",
            ["Bik"] = "БИК",
        };

        if (acronyms.TryGetValue(key, out var full)) return full;

        foreach (var item in acronyms)
        {
            if (key.Contains(item.Key, StringComparison.OrdinalIgnoreCase))
            {
                key = key.Replace(item.Key, item.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        var chars = new List<char>();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1])) chars.Add(' ');
            chars.Add(c);
        }

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
