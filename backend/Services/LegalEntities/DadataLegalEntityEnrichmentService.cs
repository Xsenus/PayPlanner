using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Services.LegalEntities
{
    public class DadataLegalEntityEnrichmentService : ILegalEntityEnrichmentService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly HttpClient _http;
        private readonly ILogger<DadataLegalEntityEnrichmentService> _logger;
        private readonly DadataOptions _opt;

        public DadataLegalEntityEnrichmentService(HttpClient httpClient, IOptions<DadataOptions> options,
            ILogger<DadataLegalEntityEnrichmentService> logger)
        {
            _http = httpClient;
            _logger = logger;
            _opt = options.Value;

            _http.Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds);

            if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<T?> CleanOneAsync<T>(string source, string kind, Func<JsonElement, T?> mapper,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(source))
                return default;

            var token = _opt.ApiKey;
            var secret = _opt.Secret;
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogDebug("DaData cleaner not configured (token/secret), skipping {Kind}", kind);
                return default;
            }

            // cleaner ожидает массив строк: [ "value" ]
            var url = Combine(_opt.CleanerUrl, kind); // e.g. .../clean/address
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent($"[ {JsonSerializer.Serialize(source)} ]", Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            req.Headers.Add("X-Secret", secret);

            using var resp = await SendWithRetriesAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("DaData clean/{Kind} failed: {Code} {Reason}", kind, (int)resp.StatusCode, resp.ReasonPhrase);
                return default;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return default;

            return mapper(arr[0]);
        }

        private static string Combine(string baseUrl, string relative)
        {
            baseUrl = baseUrl?.Trim() ?? "";
            if (!baseUrl.EndsWith("/", StringComparison.Ordinal)) baseUrl += "/";
            return baseUrl + relative.TrimStart('/');
        }

        private static DateTimeOffset? FromEpochMs(long? ms) =>
            ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;

        private static StringContent JsonContent(object payload) =>
            new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        private static CleanedAddress? MapAddress(JsonElement e) =>
            new(
                Result: e.GetPropertyOrDefault("result"),
                PostalCode: e.GetPropertyOrDefault("postal_code"),
                Country: e.GetPropertyOrDefault("country"),
                RegionWithType: e.GetPropertyOrDefault("region_with_type"),
                CityWithType: e.GetPropertyOrDefault("city_with_type"),
                StreetWithType: e.GetPropertyOrDefault("street_with_type"),
                House: e.GetPropertyOrDefault("house"),
                Flat: e.GetPropertyOrDefault("flat"),
                FiasId: e.GetPropertyOrDefault("fias_id"),
                KladrId: e.GetPropertyOrDefault("kladr_id"),
                GeoLat: e.GetPropertyOrDefault("geo_lat"),
                GeoLon: e.GetPropertyOrDefault("geo_lon")
            );

        private static CleanedEmail? MapEmail(JsonElement e)
        {
            var email = e.GetPropertyOrDefault("email");
            if (string.IsNullOrWhiteSpace(email)) return null;
            return new CleanedEmail(
                Email: email,
                Type: e.GetPropertyOrDefault("type"),
                Qc: e.GetIntOrNull("qc")
            );
        }

        private static CleanedPhone? MapPhone(JsonElement e)
        {
            var phone = e.GetPropertyOrDefault("phone");
            if (string.IsNullOrWhiteSpace(phone)) return null;
            return new CleanedPhone(
                Phone: phone,
                Country: e.GetPropertyOrDefault("country"),
                City: e.GetPropertyOrDefault("city"),
                Provider: e.GetPropertyOrDefault("provider"),
                Qc: e.GetIntOrNull("qc")
            );
        }

        private async Task<HttpResponseMessage> SendWithRetriesAsync(HttpRequestMessage req, CancellationToken ct)
        {
            const int maxAttempts = 3;
            var attempt = 0;
            HttpResponseMessage? resp = null;

            while (true)
            {
                attempt++;
                resp = await _http.SendAsync(req.Clone(), ct);
                if (IsTransient(resp) && attempt < maxAttempts)
                {
                    var delayMs = 200 * attempt * attempt; // квадратичная пауза
                    _logger.LogDebug("Retry {Attempt} for {Url}, status {Code}", attempt, req.RequestUri, (int)resp.StatusCode);
                    await Task.Delay(delayMs, ct);
                    continue;
                }
                return resp;
            }

            static bool IsTransient(HttpResponseMessage r) =>
                r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                               or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        }

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken ct) =>
            CleanOneAsync(rawAddress, "address", MapAddress, ct);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken ct) =>
            CleanOneAsync(rawEmail, "email", MapEmail, ct);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken ct) =>
            CleanOneAsync(rawPhone, "phone", MapPhone, ct);

        public async Task<LegalEntityDetails?> FindByInnOrOgrnAsync(
            string query, string? kpp, CancellationToken ct)
        {
            var token = _opt.ApiKey;
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(query))
                return null;

            var url = Combine(_opt.BaseUrl, "findById/party"); // POST body: { query, kpp? }
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent(new { query, kpp })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

            using var resp = await SendWithRetriesAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("DaData findById failed: {Code} {Reason}",
                    (int)resp.StatusCode, resp.ReasonPhrase);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<SuggestResponse>(stream, JsonOptions, ct);
            var s = result?.Suggestions?.FirstOrDefault()?.Data;
            if (s == null) return null;

            return new LegalEntityDetails(
                ShortName: s.Name?.ShortWithOpf ?? string.Empty,
                FullName: s.Name?.FullWithOpf ?? s.Name?.ShortWithOpf ?? result!.Suggestions![0].Value,
                Inn: s.Inn,
                Kpp: s.Kpp,
                Ogrn: s.Ogrn,
                OpfShort: s.Opf?.Short,
                OpfFull: s.Opf?.Full,
                Status: s.State?.Status,
                RegistrationDate: FromEpochMs(s.State?.RegistrationDate),
                LiquidationDate: FromEpochMs(s.State?.LiquidationDate),
                Okved: s.Okved,
                OkvedType: s.OkvedType,
                BranchCount: s.BranchCount,
                ManagementName: s.Management?.Name,
                ManagementPost: s.Management?.Post,
                AddressValue: s.Address?.Value,
                AddressUnrestrictedValue: s.Address?.UnrestrictedValue
            );
        }

        public async Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(
            string? query, string? inn, int limit, CancellationToken ct)
        {
            var token = _opt.ApiKey;
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("DaData token not configured, skipping suggestions");
                return Array.Empty<LegalEntitySuggestion>();
            }

            var effectiveLimit = Math.Clamp(limit <= 0 ? 5 : limit, 1, 20);
            var payloadQuery = !string.IsNullOrWhiteSpace(inn) ? inn!.Trim() : query?.Trim();
            if (string.IsNullOrWhiteSpace(payloadQuery))
                return Array.Empty<LegalEntitySuggestion>();

            var url = Combine(_opt.BaseUrl, "suggest/party"); // POST body: { query, count }
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent(new { query = payloadQuery, count = effectiveLimit })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

            try
            {
                using var resp = await SendWithRetriesAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("DaData suggest failed: {Code} {Reason}",
                        (int)resp.StatusCode, resp.ReasonPhrase);
                    return Array.Empty<LegalEntitySuggestion>();
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var result = await JsonSerializer.DeserializeAsync<SuggestResponse>(stream, JsonOptions, ct);
                if (result?.Suggestions == null || result.Suggestions.Count == 0)
                    return Array.Empty<LegalEntitySuggestion>();

                return result.Suggestions
                    .Select(s => new LegalEntitySuggestion(
                        s.Value ?? s.Data?.Name?.ShortWithOpf ?? string.Empty,
                        s.Data?.Name?.FullWithOpf ?? s.Value ?? s.Data?.Name?.ShortWithOpf,
                        s.Data?.Inn,
                        s.Data?.Kpp,
                        s.Data?.Ogrn,
                        s.Data?.Address?.Value,
                        s.Data?.Phones?.FirstOrDefault(),
                        s.Data?.Emails?.FirstOrDefault(),
                        s.Data?.Management?.Name))
                    .Where(x => !string.IsNullOrWhiteSpace(x.ShortName))
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DaData suggest error");
                return Array.Empty<LegalEntitySuggestion>();
            }
        }

        private sealed class SuggestResponse
        {
            [JsonPropertyName("suggestions")]
            public List<Suggestion> Suggestions { get; set; } = new();
        }

        private sealed class Suggestion
        {

            [JsonPropertyName("data")]
            public PartyData? Data { get; set; }
            [JsonPropertyName("value")]
            public string? Value { get; set; }
        }

        private sealed class PartyData
        {

            [JsonPropertyName("address")]
            public AddressData? Address { get; set; }

            [JsonPropertyName("branch_count")]
            public int? BranchCount { get; set; }

            [JsonPropertyName("emails")]
            public List<string>? Emails { get; set; }
            [JsonPropertyName("inn")]
            public string? Inn { get; set; }

            [JsonPropertyName("kpp")]
            public string? Kpp { get; set; }

            [JsonPropertyName("management")]
            public ManagementData? Management { get; set; }

            [JsonPropertyName("name")]
            public NameData? Name { get; set; }

            [JsonPropertyName("ogrn")]
            public string? Ogrn { get; set; }

            [JsonPropertyName("okved")]
            public string? Okved { get; set; }

            [JsonPropertyName("okved_type")]
            public string? OkvedType { get; set; }

            [JsonPropertyName("opf")]
            public OpfData? Opf { get; set; }

            [JsonPropertyName("phones")]
            public List<string>? Phones { get; set; }

            [JsonPropertyName("state")]
            public StateData? State { get; set; }
        }

        private sealed class NameData
        {

            [JsonPropertyName("full")]
            public string? Full { get; set; }

            [JsonPropertyName("full_with_opf")]
            public string? FullWithOpf { get; set; }

            [JsonPropertyName("short")]
            public string? Short { get; set; }
            [JsonPropertyName("short_with_opf")]
            public string? ShortWithOpf { get; set; }
        }

        private sealed class AddressData
        {

            [JsonPropertyName("unrestricted_value")]
            public string? UnrestrictedValue { get; set; }
            [JsonPropertyName("value")]
            public string? Value { get; set; }
        }

        private sealed class ManagementData
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("post")]
            public string? Post { get; set; }
        }

        private sealed class OpfData
        {

            [JsonPropertyName("full")]
            public string? Full { get; set; }
            [JsonPropertyName("short")]
            public string? Short { get; set; }
        }

        private sealed class StateData
        {

            [JsonPropertyName("liquidation_date")]
            public long? LiquidationDate { get; set; }

            [JsonPropertyName("registration_date")]
            public long? RegistrationDate { get; set; }
            [JsonPropertyName("status")]
            public string? Status { get; set; }
        }
    }

    internal static class JsonElementExt
    {
        public static int? GetIntOrNull(this JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i
                : null;
        public static string? GetPropertyOrDefault(this JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.GetString()
                : null;
    }

    internal static class HttpRequestMessageExtensions
    {
        private static string ReadAsString(this StringContent sc) =>
            sc.ReadAsStringAsync().GetAwaiter().GetResult();

        public static HttpRequestMessage Clone(this HttpRequestMessage req)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri);

            // Content
            if (req.Content is StringContent sc)
                clone.Content = new StringContent(sc.ReadAsString(), Encoding.UTF8, sc.Headers.ContentType?.MediaType);
            else if (req.Content != null)
            {
                // универсальная копия
                var bytes = req.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(bytes);
                foreach (var h in req.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            // Headers
            foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var prop in req.Options) clone.Options.Set(new(prop.Key), prop.Value);

            return clone;
        }
    }
}