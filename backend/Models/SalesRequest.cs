using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class SalesRequestSources
{
    public const string Call = "Call";
    public const string Max = "Max";
    public const string Avito = "Avito";
    public const string Site = "Site";
    public const string Other = "Other";

    public static readonly string[] All = [Call, Max, Avito, Site, Other];
}

public static class SalesRequestTypes
{
    public const string New = "New";
    public const string Fixed = "Fixed";
    public const string PerCase = "PerCase";

    public static readonly string[] All = [New, Fixed, PerCase];
}

public static class SalesRequestFinalKinds
{
    public const string None = "None";
    public const string Success = "Success";
    public const string Failure = "Failure";
}

public static class SalesRequestEventKinds
{
    public const string Created = "Created";
    public const string StatusChanged = "StatusChanged";
    public const string FieldsChanged = "FieldsChanged";
    public const string TaskCreated = "TaskCreated";
    public const string TasksForceClosed = "TasksForceClosed";
}

/// <summary>
/// Заявка воронки продаж: от первого обращения до оплаты или отказа. Лёгкая карточка, отдельная от дела.
/// Клиент и дело хранятся ссылками без внешних ключей; имя и телефон клиента копируются для списка.
/// </summary>
public class SalesRequest
{
    [Key]
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(16)]
    public string Source { get; set; } = SalesRequestSources.Other;

    public int ClientId { get; set; }

    [MaxLength(300)]
    public string ClientName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ClientPhone { get; set; } = string.Empty;

    /// <summary>Дело клиента, к которому относится обращение (активное или закрытое).</summary>
    public int? ClientCaseId { get; set; }

    /// <summary>Дело на момент привязки было закрыто — Андрей решает, продолжение это или новое обращение.</summary>
    public bool LinkedCaseWasClosed { get; set; }

    [Required, MaxLength(16)]
    public string Type { get; set; } = SalesRequestTypes.New;

    [Required, MaxLength(4000)]
    public string Summary { get; set; } = string.Empty;

    public int StatusId { get; set; }

    public DateTime StatusEnteredAt { get; set; } = DateTime.UtcNow;

    public int ResponsibleUserId { get; set; }

    public decimal? Amount { get; set; }

    [MaxLength(2000)]
    public string? Comment { get; set; }

    public int CreatedByUserId { get; set; }

    [Required, MaxLength(16)]
    public string FinalKind { get; set; } = SalesRequestFinalKinds.None;

    public DateTime? ClosedAt { get; set; }

    public int? ClosedByUserId { get; set; }

    /// <summary>Фактический платёж в CRM, подтвердивший оплату (обязателен для успешного финала).</summary>
    public int? PaidPaymentId { get; set; }

    /// <summary>Номер версии для оптимистичной блокировки: два одновременных перехода не пройдут оба.</summary>
    public int Version { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Настраиваемый статус воронки.</summary>
public class SalesRequestStatus
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(40)]
    public string Key { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Title { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    [Required, MaxLength(16)]
    public string FinalKind { get; set; } = SalesRequestFinalKinds.None;

    public bool IsActive { get; set; } = true;
}

/// <summary>Правило «при входе в статус создать задачу».</summary>
public class SalesRequestStatusTaskRule
{
    [Key]
    public int Id { get; set; }

    public int StatusId { get; set; }

    [Required, MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Исполнитель; если не задан — ответственный по заявке.</summary>
    public int? AssigneeUserId { get; set; }

    /// <summary>Срок в минутах от момента входа в статус.</summary>
    public int DueInMinutes { get; set; } = 60;

    public bool IsActive { get; set; } = true;
}

public class SalesRequestEvent
{
    [Key]
    public int Id { get; set; }

    public int SalesRequestId { get; set; }

    [Required, MaxLength(24)]
    public string Kind { get; set; } = string.Empty;

    public int? FromStatusId { get; set; }

    public int? ToStatusId { get; set; }

    public int? UserId { get; set; }

    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
