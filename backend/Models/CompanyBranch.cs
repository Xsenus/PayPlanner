using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

/// <summary>Статус профиля собственной компании или филиала.</summary>
public static class CompanyProfileStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Archived = "Archived";

    public static readonly IReadOnlyList<string> CompanyStatuses = [Draft, Active, Archived];
    public static readonly IReadOnlyList<string> BranchStatuses = [Active, Archived];

    public static string? NormalizeCompany(string? value) => Match(CompanyStatuses, value);

    public static string? NormalizeBranch(string? value) => Match(BranchStatuses, value);

    private static string? Match(IReadOnlyList<string> allowed, string? value)
        => allowed.FirstOrDefault(item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Вид структурного подразделения собственной компании.</summary>
public static class CompanyBranchKinds
{
    public const string Branch = "Branch";
    public const string Office = "Office";
    public const string Point = "Point";
    public const string Direction = "Direction";

    public static readonly IReadOnlyList<string> All = [Branch, Office, Point, Direction];

    public static string? Normalize(string? value)
        => All.FirstOrDefault(item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Филиал, офис, точка или направление собственной компании. Не является отдельным юридическим лицом:
/// ИНН, ОГРН, название и прочие реквизиты берутся у компании. Заполненное поле здесь — явное переопределение;
/// пустое означает «наследуется от компании».
/// </summary>
public class CompanyBranch
{
    [Key]
    public int Id { get; set; }

    public int LegalEntityId { get; set; }
    public LegalEntity LegalEntity { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    public string Kind { get; set; } = CompanyBranchKinds.Branch;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? Code { get; set; }

    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = CompanyProfileStatuses.Active;

    /// <summary>Собственный КПП обособленного подразделения; пусто — используется КПП компании.</summary>
    [MaxLength(20)]
    public string? Kpp { get; set; }

    /// <summary>Переопределение адреса; пусто — адрес компании.</summary>
    [MaxLength(500)]
    public string? Address { get; set; }

    /// <summary>
    /// Идентификатор счёта из <see cref="LegalEntity.BankAccountsJson"/> этой же компании,
    /// используемый вместо основного; пусто — основной счёт компании.
    /// </summary>
    [MaxLength(64)]
    public string? BankAccountId { get; set; }

    /// <summary>Переопределение подписанта (тот же формат, что у <see cref="LegalEntity.DocumentDirectorJson"/>); пусто — подписант компании.</summary>
    [MaxLength(8000)]
    public string? SignerJson { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
