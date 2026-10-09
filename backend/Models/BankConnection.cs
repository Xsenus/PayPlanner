using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class BankProviderKeys
{
    public const string Tochka = "Tochka";
    public const string Sber = "Sber";
}

public static class BankConnectionStatuses
{
    public const string Active = "Active";
    /// <summary>Токен отозван или истёк: нужна повторная авторизация владельцем подключения.</summary>
    public const string ReauthRequired = "ReauthRequired";
    public const string Disconnected = "Disconnected";
}

/// <summary>Состояние счёта по данным банка. Исчезновение счёта из ответа не означает закрытия.</summary>
public static class BankAccountStatuses
{
    public const string Open = "Open";
    public const string Limited = "Limited";
    public const string Preliminary = "Preliminary";
    public const string Closed = "Closed";
    public const string Unavailable = "Unavailable";

    public static readonly IReadOnlyList<string> All = [Open, Limited, Preliminary, Closed, Unavailable];

    /// <summary>Счёт можно выбирать для нового документа только в открытом состоянии.</summary>
    public static bool AllowsNewDocuments(string status) => status == Open;
}

/// <summary>Подключение собственной компании к банку (OAuth). Токены хранятся только в защищённом виде.</summary>
public class BankConnection
{
    [Key]
    public int Id { get; set; }

    public int LegalEntityId { get; set; }

    [Required, MaxLength(16)]
    public string Provider { get; set; } = string.Empty;

    [Required, MaxLength(16)]
    public string Status { get; set; } = BankConnectionStatuses.Active;

    [MaxLength(4000)]
    public string? AccessTokenProtected { get; set; }

    [MaxLength(4000)]
    public string? RefreshTokenProtected { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    [MaxLength(500)]
    public string? Scopes { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public DateTime? LastAccountsSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<BankConnectionAccount> Accounts { get; set; } = new List<BankConnectionAccount>();
}

/// <summary>Счёт, полученный от банка по подключению.</summary>
public class BankConnectionAccount
{
    [Key]
    public int Id { get; set; }

    public int BankConnectionId { get; set; }
    public BankConnection BankConnection { get; set; } = null!;

    /// <summary>Идентификатор счёта у банка; устойчивый ключ сопоставления внутри подключения.</summary>
    [Required, MaxLength(128)]
    public string ExternalAccountId { get; set; } = string.Empty;

    [Required, MaxLength(34)]
    public string AccountNumber { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? Bik { get; set; }

    [MaxLength(8)]
    public string Currency { get; set; } = "RUB";

    [Required, MaxLength(16)]
    public string Status { get; set; } = BankAccountStatuses.Open;

    public bool IsHidden { get; set; }

    /// <summary>Загружать ли выписки по счёту.</summary>
    public bool StatementsEnabled { get; set; } = true;

    /// <summary>Дата, до которой операции подтверждённо сохранены; следующая загрузка идёт после неё.</summary>
    public DateTime? StatementWatermark { get; set; }

    public DateTime? LastStatementSyncAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Операция выписки в том виде, как её отдал банк; разнесение по документам выполняется отдельно.</summary>
public class BankStatementOperation
{
    [Key]
    public int Id { get; set; }

    public int BankConnectionAccountId { get; set; }
    public BankConnectionAccount BankConnectionAccount { get; set; } = null!;

    [Required, MaxLength(128)]
    public string ExternalOperationId { get; set; } = string.Empty;

    public DateTime OperationDate { get; set; }

    /// <summary>Положительная сумма; направление задаёт <see cref="IsIncome"/>.</summary>
    public decimal Amount { get; set; }

    public bool IsIncome { get; set; }

    /// <summary>Операция проведена банком; незавершённые обновляются при следующей загрузке.</summary>
    public bool IsBooked { get; set; } = true;

    [MaxLength(20)]
    public string? CounterpartyInn { get; set; }

    [MaxLength(300)]
    public string? CounterpartyName { get; set; }

    [MaxLength(34)]
    public string? CounterpartyAccount { get; set; }

    [MaxLength(2000)]
    public string? Purpose { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
