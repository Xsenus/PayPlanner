namespace PayPlanner.Api.Services.LegalEntities;

public class DadataOptions
{
    public string BaseUrl { get; set; } = "https://suggestions.dadata.ru/suggestions/api/4_1/rs";

    public string? ApiKey { get; set; }

    public string? Secret { get; set; }
}
