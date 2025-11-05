using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Linq;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public class DadataService : IDadataService
{
    private readonly HttpClient _httpClient;
    private readonly DadataOptions _options;

    public DadataService(HttpClient httpClient, IOptions<DadataOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "https://suggestions.dadata.ru/suggestions/api/4_1/rs/"
            : _options.BaseUrl;

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(_options.Secret))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Secret", _options.Secret);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CompanyLookupResponse?> FindCompanyByInnAsync(string inn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Dadata API ключ не настроен в конфигурации.");
        }

        var sanitizedInn = (inn ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sanitizedInn))
        {
            return null;
        }

        using var response = await _httpClient.PostAsJsonAsync("findById/party", new { query = sanitizedInn }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Не удалось получить данные из Dadata: {response.StatusCode}. {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<DadataPartyResponse>(cancellationToken: ct);
        var suggestion = payload?.Suggestions?.FirstOrDefault();
        if (suggestion?.Data is null)
        {
            return null;
        }

        var data = suggestion.Data;
        var address = data.Address;
        var actual = address?.Value ?? string.Empty;
        var legal = address?.UnrestrictedValue ?? actual;
        if (string.IsNullOrWhiteSpace(legal))
        {
            legal = address?.Source ?? actual;
        }

        return new CompanyLookupResponse
        {
            FullName = data.Name?.FullWithOpf ?? suggestion.Value ?? string.Empty,
            ShortName = data.Name?.ShortWithOpf ?? data.Name?.FullWithOpf ?? suggestion.Value ?? string.Empty,
            Inn = data.Inn ?? string.Empty,
            Kpp = data.Kpp ?? string.Empty,
            ActualAddress = actual,
            LegalAddress = legal,
        };
    }

    private sealed class DadataPartyResponse
    {
        [JsonPropertyName("suggestions")]
        public List<DadataPartySuggestion> Suggestions { get; set; } = new();
    }

    private sealed class DadataPartySuggestion
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("data")]
        public DadataPartyData? Data { get; set; }
    }

    private sealed class DadataPartyData
    {
        [JsonPropertyName("inn")]
        public string? Inn { get; set; }

        [JsonPropertyName("kpp")]
        public string? Kpp { get; set; }

        [JsonPropertyName("name")]
        public DadataPartyName? Name { get; set; }

        [JsonPropertyName("address")]
        public DadataPartyAddress? Address { get; set; }
    }

    private sealed class DadataPartyName
    {
        [JsonPropertyName("full_with_opf")]
        public string? FullWithOpf { get; set; }

        [JsonPropertyName("short_with_opf")]
        public string? ShortWithOpf { get; set; }
    }

    private sealed class DadataPartyAddress
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("unrestricted_value")]
        public string? UnrestrictedValue { get; set; }

        [JsonPropertyName("data")]
        public DadataPartyAddressData? Data { get; set; }

        public string? Source => Data?.Source;
    }

    private sealed class DadataPartyAddressData
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }
}
