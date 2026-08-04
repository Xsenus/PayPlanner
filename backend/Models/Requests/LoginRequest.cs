using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>Учётные данные для входа во внутреннюю CRM.</summary>
public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Password { get; set; } = string.Empty;
}
