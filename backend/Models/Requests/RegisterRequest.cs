using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>Данные самостоятельной регистрации пользователя CRM.</summary>
public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 2)]
    [RegularExpression(@".*\S.*", ErrorMessage = "Полное имя не может состоять только из пробелов.")]
    public string FullName { get; set; } = string.Empty;
}
