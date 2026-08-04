using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DadataLegalEntityEnrichmentServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task SuggestAsync_ReturnsEmptyForExternalHttpErrors(HttpStatusCode statusCode, int expectedAttempts)
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"message":"external error"}""", Encoding.UTF8, "application/json"),
        });
        var service = CreateService(scope.Db, handler);

        var suggestions = await service.SuggestAsync("ромашка", null, 5, CancellationToken.None);

        Assert.Empty(suggestions);
        Assert.Equal(expectedAttempts, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task SuggestAddressesAsync_ReturnsEmptyForExternalHttpErrors(HttpStatusCode statusCode, int expectedAttempts)
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"message":"external error"}""", Encoding.UTF8, "application/json"),
        });
        var service = CreateService(scope.Db, handler);

        var suggestions = await service.SuggestAddressesAsync("Новосибирск", 5, CancellationToken.None);

        Assert.Empty(suggestions);
        Assert.Equal(expectedAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task SuggestAsync_DisposesTransientResponsesBeforeRetrying()
    {
        await using var scope = await CreateDbScopeAsync();
        var attempts = 0;
        var disposedContents = 0;
        var handler = new RecordingDadataHandler(_ =>
        {
            attempts++;
            var statusCode = attempts < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            var json = statusCode == HttpStatusCode.OK
                ? """{"suggestions":[]}"""
                : """{"message":"temporary error"}""";
            return new HttpResponseMessage(statusCode)
            {
                Content = new TrackingStringContent(json, () => disposedContents++),
            };
        });
        var service = CreateService(scope.Db, handler);

        var suggestions = await service.SuggestAsync("ромашка", null, 5, CancellationToken.None);

        Assert.Empty(suggestions);
        Assert.Equal(3, attempts);
        Assert.Equal(3, disposedContents);
        foreach (var body in handler.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal("ромашка", document.RootElement.GetProperty("query").GetString());
        }
    }

    [Fact]
    public async Task CleanAddressAsync_ReturnsNullWhenDadataTimesOut()
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => throw new TaskCanceledException("DaData timeout."));
        var service = CreateService(scope.Db, handler);

        var cleaned = await service.CleanAddressAsync("Новосибирск, Красный проспект, 1", CancellationToken.None);

        Assert.Null(cleaned);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FindByInnOrOgrnAsync_ReturnsNullWhenDadataRequestFails()
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => throw new HttpRequestException("DaData unavailable."));
        var service = CreateService(scope.Db, handler);

        var details = await service.FindByInnOrOgrnAsync("7707083893", null, CancellationToken.None);

        Assert.Null(details);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SuggestAddressesAsync_PreservesCallerCancellation()
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => throw new TaskCanceledException("Caller cancelled."));
        var service = CreateService(scope.Db, handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SuggestAddressesAsync("Новосибирск", 5, cts.Token));
    }

    [Fact]
    public async Task SuggestAddressesAsync_RetriesWithNormalizedAddress_WhenCompactQueryHasNoMatches()
    {
        await using var scope = await CreateDbScopeAsync();
        var attempt = 0;
        var handler = new RecordingDadataHandler(_ =>
        {
            attempt++;
            var json = attempt == 1
                ? """{"suggestions":[]}"""
                : """
                  {
                    "suggestions": [
                      {
                        "value": "г Новокуйбышевск, ул Дзержинского, д 14, кв 9",
                        "unrestricted_value": "446200, Самарская обл, г Новокуйбышевск, ул Дзержинского, д 14, кв 9",
                        "data": {
                          "postal_code": "446200",
                          "region_with_type": "Самарская обл",
                          "city_with_type": "г Новокуйбышевск",
                          "street_with_type": "ул Дзержинского",
                          "house": "14",
                          "flat": "9",
                          "fias_id": "fias-14",
                          "kladr_id": "kladr-14"
                        }
                      }
                    ]
                  }
                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var service = CreateService(scope.Db, handler);

        var suggestions = await service.SuggestAddressesAsync(
            "г.Новокуйбышевск, ул. Дзержинского д.14.кв.9",
            5,
            CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("446200", suggestion.PostalCode);
        Assert.Equal("г Новокуйбышевск", suggestion.CityWithType);
        Assert.Equal("ул Дзержинского", suggestion.StreetWithType);
        Assert.Equal("14", suggestion.House);
        Assert.Equal("9", suggestion.Flat);
        Assert.Equal(2, handler.Requests.Count);
        using var firstBody = JsonDocument.Parse(handler.Bodies[0]);
        using var secondBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(
            "г.Новокуйбышевск, ул. Дзержинского д.14.кв.9",
            firstBody.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            "г. Новокуйбышевск, ул. Дзержинского, д 14, кв 9",
            secondBody.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task SuggestAddressesAsync_NormalizesApartmentWithoutSpace_WhenFallbackQueryIsNeeded()
    {
        await using var scope = await CreateDbScopeAsync();
        var handler = new RecordingDadataHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"suggestions":[]}""", Encoding.UTF8, "application/json"),
        });
        var service = CreateService(scope.Db, handler);

        var suggestions = await service.SuggestAddressesAsync(
            "новокуйбышевск, ул. Миронова, д. 8, кв12",
            5,
            CancellationToken.None);

        Assert.Empty(suggestions);
        Assert.Equal(2, handler.Requests.Count);
        using var firstBody = JsonDocument.Parse(handler.Bodies[0]);
        using var secondBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(
            "новокуйбышевск, ул. Миронова, д. 8, кв12",
            firstBody.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            "новокуйбышевск, ул. Миронова, д 8, кв 12",
            secondBody.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task SuggestAddressesAsync_UsesServerDadataCredentials()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = DadataLegalEntityEnrichmentService.ApiKeySettingKey,
                Value = "server-token",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
            },
            new SystemSetting
            {
                Key = DadataLegalEntityEnrichmentService.SecretSettingKey,
                Value = "server-secret",
                CreatedAt = DateTime.UtcNow.AddMinutes(-9),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-9),
            });
        await scope.Db.SaveChangesAsync();

        var handler = CreateSuccessfulAddressSuggestHandler();
        var service = CreateService(scope.Db, handler, apiKey: string.Empty, secret: string.Empty);

        var suggestions = await service.SuggestAddressesAsync(
            "новокуйбышевск, ул. Миронова, д. 8",
            5,
            CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("446213", suggestion.PostalCode);
        Assert.Single(handler.Requests);
        Assert.Equal("Token", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("server-token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SuggestAddressesAsync_UsesLegacyAdminCredentials_WhenServerKeysAreEmpty()
    {
        await using var scope = await CreateDbScopeAsync();
        var adminRole = new Role { Name = "admin", Description = "Администратор" };
        var admin = new User
        {
            Email = "admin@example.test",
            FullName = "Администратор",
            Role = adminRole,
            IsActive = true,
            IsApproved = true,
        };
        scope.Db.Users.Add(admin);
        await scope.Db.SaveChangesAsync();

        scope.Db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = admin.Id,
                Key = DadataLegalEntityEnrichmentService.ApiKeySettingKey,
                Value = "legacy-token",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
            },
            new UserSetting
            {
                UserId = admin.Id,
                Key = DadataLegalEntityEnrichmentService.SecretSettingKey,
                Value = "legacy-secret",
                CreatedAt = DateTime.UtcNow.AddMinutes(-9),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-9),
            });
        await scope.Db.SaveChangesAsync();

        var handler = CreateSuccessfulAddressSuggestHandler();
        var service = CreateService(scope.Db, handler, apiKey: string.Empty, secret: string.Empty);

        var suggestions = await service.SuggestAddressesAsync(
            "новокуйбышевск, ул. Миронова, д. 8",
            5,
            CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("446213", suggestion.PostalCode);
        Assert.Single(handler.Requests);
        Assert.Equal("Token", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("legacy-token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    private static DadataLegalEntityEnrichmentService CreateService(
        PaymentContext db,
        HttpMessageHandler handler,
        string apiKey = "test-token",
        string secret = "test-secret")
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://dadata.test/") },
            Options.Create(new DadataOptions
            {
                ApiKey = apiKey,
                Secret = secret,
                BaseUrl = "https://dadata.test/suggestions/",
                CleanerUrl = "https://dadata.test/clean/",
            }),
            NullLogger<DadataLegalEntityEnrichmentService>.Instance,
            db);

    private static RecordingDadataHandler CreateSuccessfulAddressSuggestHandler()
        => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "suggestions": [
                    {
                      "value": "г Новокуйбышевск, ул Миронова, д 8",
                      "unrestricted_value": "446213, Самарская обл, г Новокуйбышевск, ул Миронова, д 8",
                      "data": {
                        "postal_code": "446213",
                        "region_with_type": "Самарская обл",
                        "city_with_type": "г Новокуйбышевск",
                        "street_with_type": "ул Миронова",
                        "house": "8"
                      }
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

    private static async Task<TestDbScope> CreateDbScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();

        return new TestDbScope(connection, db);
    }

    private sealed class RecordingDadataHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class TrackingStringContent(string content, Action onDispose)
        : StringContent(content, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestDbScope(SqliteConnection connection, PaymentContext db) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
