namespace PayPlanner.Api.Models.Responses;

public class AuthProviderSettingsSaveResultDto
{
    public AuthProviderSettingsDto Settings { get; set; } = new();
    public string? Message { get; set; }
}
