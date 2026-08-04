using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// Данные паспорта, которые разрешено изменять из карточки клиента.
/// Служебные поля шифрования намеренно отсутствуют, поэтому их нельзя
/// подменить через входной JSON.
/// </summary>
public sealed class UpsertClientPassportRequest
{
    [MaxLength(20)]
    public string? Series { get; init; }

    [MaxLength(20)]
    public string? Number { get; init; }

    [MaxLength(500)]
    public string? IssuedBy { get; init; }

    public DateTime? IssuedAt { get; init; }

    [MaxLength(20)]
    public string? DepartmentCode { get; init; }

    [MaxLength(500)]
    public string? BirthPlacePassport { get; init; }

    [MaxLength(120)]
    public string? BirthPlaceFias { get; init; }

    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
}
