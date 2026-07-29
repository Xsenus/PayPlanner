using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SudrfCourtContractFixtureTests
{
    private static readonly Uri CaseUri = new(
        "https://contract-fixture.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=100001&case_uid=00000000-0000-0000-0000-000000000001&delo_id=42");

    [Fact]
    public void StandardCaseCard_PreservesNormalizedContract()
    {
        var data = SudrfCourtSyncService.ParseCaseHtml(
            ReadFixture("case-card-standard.html"),
            CaseUri,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Тестовый областной суд", data.CourtName);
        Assert.Equal("Т-123/2026", data.CaseNumber);
        Assert.Equal("99TS0001-01-2026-000001-01", data.JudicialUid);
        Assert.Equal("appeal", data.Instance);
        Assert.Equal("Судья Тестовый Тестович", data.JudgeName);
        Assert.Equal("тестовая категория спора", data.Category);
        Assert.Equal(new DateTime(2026, 2, 10), data.ReceivedAt);
        Assert.Equal(new DateTime(2026, 3, 15, 11, 30, 0), data.NextHearingAt);
        Assert.Equal("зал 5", data.NextHearingPlace);
        Assert.Equal(3, data.MovementEvents.Count);
        Assert.Equal(3, data.Participants.Count);
        Assert.Contains(data.CourtActs, item =>
            item.Title == "Решение суда"
            && item.ActDate == new DateTime(2026, 3, 20)
            && item.Result == "Требования удовлетворены частично");
    }

    [Fact]
    public void LiveLayoutCaseCard_PreservesIdBlockContract()
    {
        var data = SudrfCourtSyncService.ParseCaseHtml(
            ReadFixture("case-card-live-layout.html"),
            CaseUri,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Тестовый областной суд", data.CourtName);
        Assert.Equal("Т-123/2026", data.CaseNumber);
        Assert.Equal("Тестовый районный суд", data.LowerCourtName);
        Assert.Equal("Т-100/2025", data.LowerCourtCaseNumber);
        Assert.Equal(2, data.MovementEvents.Count);
        Assert.Equal(3, data.Participants.Count);
        Assert.Contains(data.Participants, item =>
            item.Role == "АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК"
            && item.Name == "Тестовый государственный орган");
    }

    [Fact]
    public async Task SearchFixtures_PreserveDiscoveryAndCardReadContract()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var handler = new FixtureResponseHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return ReadFixture("search-results.html");
            }

            return request.RequestUri?.Query.Contains("name_op=case", StringComparison.OrdinalIgnoreCase) == true
                ? ReadFixture("case-card-standard.html")
                : ReadFixture("search-page.html");
        });
        using var client = new HttpClient(handler);
        var service = new SudrfCourtSyncService(client, NullLogger<SudrfCourtSyncService>.Instance);

        var data = await service.FetchCaseAsync(
            "https://contract-fixture.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=sf&delo_id=42",
            CancellationToken.None,
            "Т-123/2026");

        Assert.Equal("Т-123/2026", data.CaseNumber);
        Assert.Equal(3, handler.Requests.Count);
        var searchRequest = Assert.Single(handler.Requests, item => item.Method == HttpMethod.Post);
        Assert.Contains("p2_case__CASE_NUMBERSS=%D0%A2-123%2F2026", searchRequest.Body, StringComparison.Ordinal);
        Assert.Equal(CaseUri, handler.Requests[^1].Uri);
    }

    [Fact]
    public void Fixtures_AreDocumentedAndDoNotContainKnownSourceIdentifiers()
    {
        var fixtureNames = new[]
        {
            "case-card-standard.html",
            "case-card-live-layout.html",
            "search-page.html",
            "search-results.html",
        };
        var combined = string.Join(Environment.NewLine, fixtureNames.Select(ReadFixture));
        var readme = ReadFixture("README.md");

        Assert.Contains("обезлич", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Шафорост", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Казарикова", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("33а-3390/2026", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("27RS0005-01-2025-002659-56", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "CourtSync",
            "Sudrf",
            fileName));

    private sealed class FixtureResponseHandler(Func<HttpRequestMessage, string> resolveHtml) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

            var bytes = Encoding.GetEncoding("windows-1251").GetBytes(resolveHtml(request));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new("text/html") { CharSet = "windows-1251" };
            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);
}
