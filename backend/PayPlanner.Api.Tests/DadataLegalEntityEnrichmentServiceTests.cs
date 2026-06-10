using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
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

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.SuggestAddressesAsync("Новосибирск", 5, cts.Token));
    }

    private static DadataLegalEntityEnrichmentService CreateService(PaymentContext db, HttpMessageHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://dadata.test/") },
            Options.Create(new DadataOptions
            {
                ApiKey = "test-token",
                Secret = "test-secret",
                BaseUrl = "https://dadata.test/suggestions/",
                CleanerUrl = "https://dadata.test/clean/",
            }),
            NullLogger<DadataLegalEntityEnrichmentService>.Instance,
            db,
            new HttpContextAccessor());

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
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
