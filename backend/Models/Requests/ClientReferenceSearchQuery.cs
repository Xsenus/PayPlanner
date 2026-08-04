using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// Параметры ограниченного поиска по классификаторам клиента.
/// Небольшой предел защищает карточку и API от случайной полной выгрузки
/// справочников ОКИН, ОКСО и ОКПДТР.
/// </summary>
public sealed class ClientReferenceSearchQuery
{
    [MaxLength(100)]
    public string? Search { get; init; }

    [Range(1, 50)]
    public int Limit { get; init; } = 30;
}
