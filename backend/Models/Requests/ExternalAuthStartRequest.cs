using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>Параметры запуска входа или привязки через внешний OAuth-провайдер.</summary>
public class ExternalAuthStartRequest
{
    [Required(ErrorMessage = "Укажите способ входа.")]
    [StringLength(20, ErrorMessage = "Название способа входа не должно превышать 20 символов.")]
    public string Provider { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "Адрес возврата не должен превышать 1000 символов.")]
    public string? ReturnUrl { get; set; }

    [RegularExpression("(?i)^(signin|link)$", ErrorMessage = "Допустимые действия: signin или link.")]
    public string? Intent { get; set; }
}
