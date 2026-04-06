namespace PayPlanner.Api.Models.Requests;

public class ExternalOAuthProviderSettingsUpdateRequest
{
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
