using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class TaxRegisterStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Archived = "Archived";
}

public static class TaxRegisterTypes
{
    public const string Income = "Income";
    public const string Expense = "Expense";

    /// <summary>Исправительная запись: отдельная строка с обратным знаком, исходная запись не меняется.</summary>
    public const string Correction = "Correction";
}

public static class TaxRegisterActions
{
    public const string Created = "Created";
    public const string Approved = "Approved";
    public const string Corrected = "Corrected";
    public const string Archived = "Archived";
    public const string YearClosed = "YearClosed";
}

/// <summary>
/// Запись налогового регистра (журнал операций для книги доходов и расходов). Источник правды для книги:
/// в неё попадают только утверждённые записи. Дата — дата фактического движения денег, а не документа.
/// Утверждённая запись не редактируется: ошибка исправляется записью-корректировкой с обратным знаком и новой верной записью.
/// </summary>
public class TaxRegisterEntry
{
    [Key]
    public int Id { get; set; }

    public int LegalEntityId { get; set; }

    /// <summary>Счёт, по которому прошла операция (счёт банковского подключения), если операция из выписки.</summary>
    public int? BankConnectionAccountId { get; set; }

    /// <summary>Операция выписки — источник записи. Одна операция даёт не более одной записи (защита от дублей).</summary>
    public int? BankStatementOperationId { get; set; }

    /// <summary>Дата фактического движения денег (календарная дата).</summary>
    public DateTime OperationDate { get; set; }

    /// <summary>Дата создания записи в CRM — для аудита.</summary>
    public DateTime PostingAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(16)]
    public string Type { get; set; } = TaxRegisterTypes.Income;

    /// <summary>Графа книги, к которой относится запись: Income или Expense (у корректировки — как у исправляемой записи).</summary>
    [Required, MaxLength(16)]
    public string Direction { get; set; } = TaxRegisterTypes.Income;

    [MaxLength(32)]
    public string? SubType { get; set; }

    /// <summary>Сумма всегда положительная; знак хранится отдельно.</summary>
    public decimal Amount { get; set; }

    /// <summary>1 — обычная запись, -1 — сторно.</summary>
    public int Sign { get; set; } = 1;

    /// <summary>Ссылка на первичный документ («Счёт № …», «Акт № …», платёжное поручение) — обязательна для утверждения.</summary>
    [MaxLength(300)]
    public string? DocumentRef { get; set; }

    /// <summary>Идентификатор документа CRM (платёж/счёт), если он известен.</summary>
    public int? DocumentId { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? Category { get; set; }

    /// <summary>Включён ли НДС в сумму (для корректной налоговой базы).</summary>
    public bool TaxBaseIncludesVat { get; set; }

    [Required, MaxLength(16)]
    public string Status { get; set; } = TaxRegisterStatuses.Draft;

    /// <summary>Для корректировки — исправляемая запись.</summary>
    public int? CorrectsEntryId { get; set; }

    /// <summary>Для исправленной записи — корректировка, которая её сторнировала. Защищает от двойного исправления.</summary>
    public int? CorrectedByEntryId { get; set; }

    [MaxLength(1000)]
    public string? CorrectionReason { get; set; }

    public int CreatedByUserId { get; set; }

    public int? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }

    /// <summary>Запись требует уточнения (расхождение с документом, возможный дубль): утверждать нужно осознанно.</summary>
    public bool NeedsClarification { get; set; }
}

/// <summary>История изменений записи регистра: любое изменение статуса фиксируется.</summary>
public class TaxRegisterEvent
{
    [Key]
    public int Id { get; set; }

    public int TaxRegisterEntryId { get; set; }

    [Required, MaxLength(16)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? FromStatus { get; set; }

    [MaxLength(16)]
    public string? ToStatus { get; set; }

    public int UserId { get; set; }

    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Закрытый отчётный год компании: после закрытия записи года не утверждаются и не создаются.</summary>
public class TaxRegisterYearClose
{
    [Key]
    public int Id { get; set; }

    public int LegalEntityId { get; set; }

    public int Year { get; set; }

    public DateTime ClosedAt { get; set; } = DateTime.UtcNow;

    public int ClosedByUserId { get; set; }

    /// <summary>Контрольная сумма данных года на момент закрытия (SHA-256).</summary>
    [Required, MaxLength(64)]
    public string DataHash { get; set; } = string.Empty;
}
