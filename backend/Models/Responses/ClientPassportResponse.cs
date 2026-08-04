namespace PayPlanner.Api.Models.Responses;

/// <summary>
/// Разрешённое представление паспорта для интерфейса CRM.
/// Зашифрованные значения и blind-index не покидают backend.
/// </summary>
public sealed record ClientPassportResponse(
    int Id,
    int ClientId,
    string? Series,
    string? Number,
    string? IssuedBy,
    DateTime? IssuedAt,
    string? DepartmentCode,
    string? BirthPlacePassport,
    string? BirthPlaceFias,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    bool IsPrimary,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
