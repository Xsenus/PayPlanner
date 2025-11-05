namespace PayPlanner.Api.Services;

public class DadataOptions
{
    public string BaseUrl { get; set; } = "https://suggestions.dadata.ru/suggestions/api/4_1/rs/";
    public string ApiKey { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
}
