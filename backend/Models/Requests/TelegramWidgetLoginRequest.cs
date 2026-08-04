using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// Подписанные Telegram поля виджета входа. Сервер повторно проверяет время и HMAC,
/// поэтому значения нельзя считать доверенными до завершения проверки подписи.
/// </summary>
public class TelegramWidgetLoginRequest
{
    [JsonPropertyName("id")]
    [Required(ErrorMessage = "Telegram не передал идентификатор пользователя.")]
    [RegularExpression("^[0-9]{1,20}$", ErrorMessage = "Идентификатор Telegram имеет неверный формат.")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    [StringLength(256)]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    [StringLength(256)]
    public string? LastName { get; set; }

    [JsonPropertyName("username")]
    [StringLength(100)]
    public string? Username { get; set; }

    [JsonPropertyName("photo_url")]
    [StringLength(1000)]
    [Url(ErrorMessage = "Ссылка на фотографию Telegram имеет неверный формат.")]
    public string? PhotoUrl { get; set; }

    [JsonPropertyName("auth_date")]
    [Required(ErrorMessage = "Telegram не передал время авторизации.")]
    [RegularExpression("^[0-9]{1,20}$", ErrorMessage = "Время авторизации Telegram имеет неверный формат.")]
    public string AuthDate { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    [Required(ErrorMessage = "Telegram не передал подпись запроса.")]
    [RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "Подпись Telegram имеет неверный формат.")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("intent")]
    [RegularExpression("(?i)^(signin|link)$", ErrorMessage = "Допустимые действия: signin или link.")]
    public string? Intent { get; set; }
}
