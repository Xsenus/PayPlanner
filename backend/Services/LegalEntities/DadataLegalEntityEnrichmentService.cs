using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.LegalEntities;

public class DadataLegalEntityEnrichmentService : ILegalEntityEnrichmentService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DadataLegalEntityEnrichmentService> _logger;
    private readonly DadataOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public DadataLegalEntityEnrichmentService(
        HttpClient httpClient,
        IOptions<DadataOptions> options,
        ILogger<DadataLegalEntityEnrichmentService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            var baseUrl = _options.BaseUrl.Trim();
            if (!baseUrl.EndsWith('/', StringComparison.Ordinal))
            {
                baseUrl += "/";
            }

            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(
        string? query,
        string? inn,
        int limit,
        CancellationToken cancellationToken)
    {
        var token = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("DaData token not configured, skipping suggestions");
            return Array.Empty<LegalEntitySuggestion>();
        }

        var effectiveLimit = Math.Clamp(limit <= 0 ? 5 : limit, 1, 20);
        var payloadQuery = !string.IsNullOrWhiteSpace(inn) ? inn!.Trim() : query?.Trim();
        if (string.IsNullOrWhiteSpace(payloadQuery))
        {
            return Array.Empty<LegalEntitySuggestion>();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "suggest/party")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                query = payloadQuery,
                count = effectiveLimit,
            }), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        if (!string.IsNullOrWhiteSpace(_options.Secret))
        {
            request.Headers.Add("X-Secret", _options.Secret);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DaData suggestions request failed: {Status} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
                return Array.Empty<LegalEntitySuggestion>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<DadataResponse>(stream, JsonOptions, cancellationToken);
            if (result?.Suggestions == null || result.Suggestions.Count == 0)
            {
                return Array.Empty<LegalEntitySuggestion>();
            }

            return result.Suggestions
                .Select(s => new LegalEntitySuggestion(
                    s.Value ?? s.Data?.Name?.ShortWithOpf ?? string.Empty,
                    s.Data?.Name?.FullWithOpf,
                    s.Data?.Inn,
                    s.Data?.Kpp,
                    s.Data?.Ogrn,
                    s.Data?.Address?.Value,
                    s.Data?.Phones?.FirstOrDefault(),
                    s.Data?.Emails?.FirstOrDefault(),
                    s.Data?.Management?.Name))
                .Where(s => !string.IsNullOrWhiteSpace(s.ShortName))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch suggestions from DaData");
            return Array.Empty<LegalEntitySuggestion>();
        }
    }

    private sealed class DadataResponse
    {
        public List<DadataSuggestion> Suggestions { get; set; } = new();
    }

    private sealed class DadataSuggestion
    {
        public string? Value { get; set; }
        public DadataPartyData? Data { get; set; }
    }

    private sealed class DadataPartyData
    {
        public string? Inn { get; set; }
        public string? Kpp { get; set; }
        public string? Ogrn { get; set; }
        public DadataName? Name { get; set; }
        public DadataAddress? Address { get; set; }
        public DadataManagement? Management { get; set; }
        public List<string>? Phones { get; set; }
        public List<string>? Emails { get; set; }
    }

    private sealed class DadataName
    {
        public string? ShortWithOpf { get; set; }
        public string? FullWithOpf { get; set; }
    }

    private sealed class DadataAddress
    {
        public string? Value { get; set; }
    }

    private sealed class DadataManagement
    {
        public string? Name { get; set; }
    }
}
