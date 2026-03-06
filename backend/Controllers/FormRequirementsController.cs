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

    private sealed record FieldDef(string Key, string Label);
    private sealed record SectionDef(string Key, string Title, Type ModelType, IReadOnlyList<FieldDef> Fields);

    private static readonly SectionDef[] Sections =
    {
        new(
            "clients",
            "Клиенты",
            typeof(Client),
            new List<FieldDef>
            {
                new("Name", "Имя"),
                new("Email", "Email"),
                new("Phone", "Телефон"),
                new("Notes", "Комментарий"),
            }),
        new(
            "legal-entities",
            "Юридические лица",
            typeof(LegalEntityRequest),
            new List<FieldDef>
            {
                new("ShortName", "Краткое название"),
                new("FullName", "Полное название"),
                new("Inn", "ИНН"),
                new("Kpp", "КПП"),
                new("Ogrn", "ОГРН"),
                new("Director", "Руководитель"),
                new("Email", "Email"),
                new("Phone", "Телефон"),
                new("Address", "Адрес"),
                new("Oktmo", "ОКТМО"),
                new("Okato", "ОКАТО"),
                new("Okpo", "ОКПО"),
                new("Okved", "ОКВЭД"),
                new("TaxOfficeCode", "Код налоговой"),
                new("TaxRegion", "Регион налоговой"),
                new("IfnsName", "Наименование ИФНС"),
                new("TreasuryInn", "ИНН получателя"),
                new("TreasuryKpp", "КПП получателя"),
                new("TreasuryBankBik", "БИК банка"),
                new("TreasuryAccountNumber", "Счет получателя"),
            }),
        new(
            "payments",
            "Платежи",
            typeof(Payment),
            new List<FieldDef>
            {
                new("Date", "Ожидаемая дата"),
                new("PlannedDate", "Плановая дата"),
                new("Amount", "Сумма"),
                new("PaidAmount", "Оплаченная сумма"),
                new("Description", "Описание"),
                new("Notes", "Комментарий"),
                new("Account", "Счет"),
                new("AccountDate", "Дата счета"),
            }),
        new(
            "contract-invoices",
            "Счета договора",
            typeof(UpsertInvoiceRequest),
            new List<FieldDef>
            {
                new("Number", "Номер счета"),
                new("Date", "Дата выставления"),
                new("DueDate", "Срок оплаты"),
                new("Amount", "Сумма"),
                new("Description", "Основание"),
                new("ActReference", "Ссылка на акт"),
                new("CounterpartyName", "Контрагент"),
            }),
        new(
            "acts",
            "Акты",
            typeof(UpsertActRequest),
            new List<FieldDef>
            {
                new("Number", "Номер акта"),
                new("Date", "Дата акта"),
                new("Amount", "Сумма"),
                new("Title", "Название"),
                new("InvoiceNumber", "Номер счета"),
                new("CounterpartyInn", "ИНН контрагента"),
                new("Comment", "Комментарий"),
            }),
        new(
            "contracts",
            "Договоры",
            typeof(UpsertContractRequest),
            new List<FieldDef>
            {
                new("Number", "Номер договора"),
                new("Date", "Дата договора"),
                new("Title", "Название"),
                new("Description", "Описание"),
                new("Amount", "Сумма"),
                new("ValidUntil", "Действует до"),
                new("StartDate", "Дата начала"),
                new("TerminationDate", "Дата расторжения"),
                new("Clients", "Клиенты"),
            }),
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
        var data = Sections.Select(section =>
        {
            var props = section.ModelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            var fields = section.Fields
                .Where(f => props.ContainsKey(f.Key))
                .Select(f => new FormRequirementFieldMetadataDto { Key = f.Key, Label = f.Label })
                .ToList();

            return new FormRequirementSectionMetadataDto
            {
                Key = section.Key,
                Title = section.Title,
                Fields = fields,
            };
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
}
