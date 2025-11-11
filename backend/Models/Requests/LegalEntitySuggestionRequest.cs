using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class LegalEntitySuggestionRequest
{
    /// <summary>
    /// Строка запроса (название, адрес, ИНН и т. п.).
    /// </summary>
    [MaxLength(500)]
    public string? Query { get; set; }

    /// <summary>
    /// ИНН, по которому нужно выполнить поиск.
    /// </summary>
    [MaxLength(20)]
    public string? Inn { get; set; }

    /// <summary>
    /// Ограничение количества результатов. Значение обрезается до диапазона 1..20.
    /// </summary>
    [Range(1, 20)]
    public int Limit { get; set; } = 5;
}
