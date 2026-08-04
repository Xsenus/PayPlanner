using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>Параметры запуска авторизации через диалог с Telegram-ботом.</summary>
public class TelegramBotAuthStartRequest
{
    [StringLength(1000, ErrorMessage = "Адрес возврата не должен превышать 1000 символов.")]
    public string? ReturnUrl { get; set; }

    [RegularExpression("(?i)^(signin|link)$", ErrorMessage = "Допустимые действия: signin или link.")]
    public string? Intent { get; set; }
}
