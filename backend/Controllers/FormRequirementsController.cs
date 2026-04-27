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

    private sealed record FieldDef(string Key, string Label, string Category = "Основные");
    private sealed record SectionDef(string Key, string Title, IReadOnlyList<FieldDef> Fields);

    private static readonly SectionDef[] Sections =
    {
        new(
            "clients-modal",
            "Клиенты (модалка создания)",
            new List<FieldDef>
            {
                new("LastName", "Фамилия"),
                new("FirstName", "Имя"),
                new("MiddleName", "Отчество"),
                new("Comment", "Комментарий"),
                new("Phone", "Телефон"),
            }),
        new(
            "clients-modal-legal",
            "Юридическое лицо (форма)",
            new List<FieldDef>
            {
                new("Inn", "ИНН"),
                new("ShortName", "Наименование"),
                new("Comment", "Комментарий"),
            }),
        new(
            "clients",
            "Клиенты (карточка редактирования)",
            new List<FieldDef>
            {
                new("Name", "Имя", "Основные"),
                new("Company", "Компания", "Основные"),
                new("Address", "Адрес", "Основные"),
                new("Notes", "Комментарий", "Основные"),
                new("IsActive", "Активный клиент", "Основные"),
                new("LegalEntityId", "Юридическое лицо", "Основные"),
                new("ResponsibleUserId", "Ответственный", "Основные"),
                new("ClientStatusId", "Статус клиента", "Основные"),

                new("LastName", "Фамилия", "Персональные данные"),
                new("FirstName", "Имя", "Персональные данные"),
                new("MiddleName", "Отчество", "Персональные данные"),
                new("Gender", "Пол", "Персональные данные"),
                new("BirthDate", "Дата рождения", "Персональные данные"),
                new("Inn", "ИНН", "Персональные данные"),
                new("Snils", "СНИЛС", "Персональные данные"),

                new("Phones", "Телефоны", "Контакты (списки)"),
                new("Emails", "Emails", "Контакты (списки)"),
                new("Addresses", "Адреса", "Контакты (списки)"),
                new("AddressEntries", "Нормализованные адреса", "Контакты (списки)"),

                new("Passports", "Паспорта", "Документы (списки)"),
                new("PassportEntries", "Паспортные данные", "Документы (списки)"),
                new("BirthCertificates", "Свидетельства о рождении", "Документы (списки)"),
                new("BirthCertificateEntries", "Свидетельства (детали)", "Документы (списки)"),
                new("MilitaryIds", "Военные билеты", "Документы (списки)"),
                new("MilitaryIdEntries", "Военные билеты (детали)", "Документы (списки)"),

                new("Education", "Образование", "Профиль (списки)"),
                new("Professions", "Профессии", "Профиль (списки)"),
                new("Workplaces", "Работа и должности", "Профиль (списки)"),
                new("Relatives", "Родственники", "Профиль (списки)"),
                new("PowersOfAttorney", "Доверенности", "Профиль (списки)"),

                new("Comment", "Комментарий к расширенной карточке", "Прочее"),
                new("LegacyNotes", "Исторические заметки", "Прочее"),
            }),
        new(
            "legal-entities",
            "Юридические лица",
            new List<FieldDef>
            {
                new("ShortName", "Краткое название", "Основные"),
                new("FullName", "Полное название", "Основные"),
                new("Inn", "ИНН", "Основные"),
                new("Kpp", "КПП", "Основные"),
                new("Ogrn", "ОГРН", "Основные"),
                new("Director", "Руководитель", "Основные"),
                new("ResponsibleUserId", "Ответственный", "Основные"),
                new("Email", "Email", "Основные"),
                new("Phone", "Телефон", "Основные"),
                new("Address", "Адрес", "Основные"),

                new("Country", "Страна", "Реквизиты: Основные"),
                new("BaseCurrency", "Базовая валюта", "Реквизиты: Основные"),
                new("ServiceLanguage", "Язык сервиса", "Реквизиты: Основные"),
                new("OrganizationForm", "Форма организации", "Реквизиты: Основные"),
                new("Timezone", "Часовой пояс", "Реквизиты: Основные"),

                new("TaxSystem", "Система налогообложения", "Реквизиты: Налогообложение"),
                new("TaxSystemMode", "Режим налогообложения", "Реквизиты: Налогообложение"),
                new("IncomeTaxRate", "Ставка (доход)", "Реквизиты: Налогообложение"),
                new("IncomeExpenseTaxRate", "Ставка (доход-расход)", "Реквизиты: Налогообложение"),
                new("CharterCapital", "Уставный капитал", "Реквизиты: Налогообложение"),
                new("TaxRegistrationDate", "Дата постановки на учет", "Реквизиты: Налогообложение"),
                new("TaxOfficeCode", "Код налоговой", "Реквизиты: Налогообложение"),
                new("TaxRegion", "Регион налоговой", "Реквизиты: Налогообложение"),
                new("IfnsName", "Наименование ИФНС", "Реквизиты: Налогообложение"),
                new("TaxOfficePhone", "Телефон налоговой", "Реквизиты: Налогообложение"),
                new("VatWithoutVat", "Без НДС", "Реквизиты: Налогообложение"),

                new("Okved", "ОКВЭД", "Реквизиты: Коды и фонды"),
                new("Okpo", "ОКПО", "Реквизиты: Коды и фонды"),
                new("Okato", "ОКАТО", "Реквизиты: Коды и фонды"),
                new("Oktmo", "ОКТМО", "Реквизиты: Коды и фонды"),
                new("Okogu", "ОКОГУ", "Реквизиты: Коды и фонды"),
                new("Okfs", "ОКФС", "Реквизиты: Коды и фонды"),
                new("Okopf", "ОКОПФ", "Реквизиты: Коды и фонды"),
                new("Pfr", "ПФР", "Реквизиты: Коды и фонды"),
                new("Fss", "ФСС", "Реквизиты: Коды и фонды"),

                new("TreasuryInn", "ИНН получателя (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryKpp", "КПП получателя (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryRecipientName", "Получатель (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryBankBik", "БИК банка (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryBankName", "Банк (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryAccountNumber", "Счет получателя (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryCorrAccountNumber", "Кор. счет (ЕНС)", "Реквизиты: ЕНС"),
                new("TreasuryKbk", "КБК (ЕНС)", "Реквизиты: ЕНС"),

                new("RequisitesEnglishJson", "Реквизиты на английском", "Реквизиты: На английском"),
                new("Addresses", "Адреса", "Реквизиты: Адреса"),
                new("ContactPhonesJson", "Телефоны", "Реквизиты: Контакты"),
                new("ContactEmailsJson", "Emails", "Реквизиты: Контакты"),

                new("LogoDataUrl", "Логотип", "Файлы/подписи"),
                new("SealDataUrl", "Печать", "Файлы/подписи"),
                new("DirectorSignatureDataUrl", "Подпись руководителя", "Файлы/подписи"),
                new("DocumentDirectorJson", "Руководитель (документы)", "Файлы/подписи"),
                new("ChiefAccountantJson", "Главный бухгалтер (документы)", "Файлы/подписи"),

                new("BankAccountsJson", "Банковские счета", "Прочее"),
                new("CashRegistersJson", "Кассы", "Прочее"),
                new("CardsJson", "Карты", "Прочее"),
                new("Notes", "Примечание", "Прочее"),
            }),
        new(
            "payments",
            "Платежи",
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
            new List<FieldDef>
            {
                new("Number", "Номер счета"),
                new("Date", "Дата выставления"),
                new("DueDate", "Срок оплаты"),
                new("Amount", "Сумма"),
                new("Description", "Основание"),
                new("ActReference", "Ссылка на акт"),
            }),
        new(
            "acts",
            "Акты",
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
        new(
            "contacts-client",
            "Контакты: клиенты",
            new List<FieldDef>
            {
                new("FullName", "ФИО", "Основные"),
                new("Phones", "Телефоны", "Контакты"),
                new("Emails", "Email", "Контакты"),
                new("LegalEntityId", "Организация", "Связи"),
                new("Position", "Должность", "Связи"),
                new("PowerOfAttorney", "Доверенность", "Связи"),
                new("SyncToPhonebook", "Участвует в синхронизации", "Телефония"),
            }),
        new(
            "contacts-external",
            "Контакты: сторонние",
            new List<FieldDef>
            {
                new("FullName", "ФИО", "Основные"),
                new("Phones", "Телефоны", "Контакты"),
                new("Emails", "Email", "Контакты"),
                new("LegalEntityId", "Организация", "Связи"),
                new("Position", "Должность", "Связи"),
                new("PowerOfAttorney", "Доверенность", "Связи"),
                new("ResponsibleUserId", "Ответственный", "Связи"),
                new("IsActive", "Активен", "Статус"),
                new("SyncToPhonebook", "Участвует в синхронизации", "Телефония"),
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
                FieldKey = NormalizeVisibleFieldKey(x.Section, x.FieldKey),
                IsRequired = x.IsRequired,
                UpdatedAt = x.UpdatedAt,
            })
            .ToListAsync(ct);

        data = data
            .GroupBy(x => $"{x.Section}:{x.FieldKey}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.UpdatedAt).First())
            .OrderBy(x => x.Section)
            .ThenBy(x => x.FieldKey)
            .ToList();

        return Ok(data);
    }

    [HttpGet("sections")]
    public ActionResult<List<FormRequirementSectionMetadataDto>> GetSections()
    {
        var data = Sections.Select(section => new FormRequirementSectionMetadataDto
        {
            Key = section.Key,
            Title = section.Key == "clients-modal" ? "Физическое лицо (форма)" : section.Title,
            Fields = section.Fields
                .Select(f => new FormRequirementFieldMetadataDto { Key = f.Key, Label = f.Label, Category = f.Category })
                .ToList(),
        }).ToList();

        return Ok(data);
    }

    private static string NormalizeVisibleFieldKey(string section, string fieldKey)
    {
        if (string.Equals(section, "clients-modal", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(fieldKey, "Name", StringComparison.OrdinalIgnoreCase)) return "FirstName";
            if (string.Equals(fieldKey, "Notes", StringComparison.OrdinalIgnoreCase)) return "Comment";
        }

        if (string.Equals(section, "clients-modal-legal", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fieldKey, "Notes", StringComparison.OrdinalIgnoreCase))
        {
            return "Comment";
        }

        return fieldKey;
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
