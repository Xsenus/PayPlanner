using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SudrfCourtSyncServiceTests
{
    [Fact]
    public void ParseCaseHtml_ExtractsCaseCardFieldsAndHearing()
    {
        var data = SudrfCourtSyncService.ParseCaseHtml(
            BuildCourtHtml(),
            new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"));

        Assert.Equal("Хабаровский краевой суд", data.CourtName);
        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.Equal("appeal", data.Instance);
        Assert.Equal("Казарикова Оксана Витальевна", data.JudgeName);
        Assert.Equal(new DateTime(2026, 4, 16), data.ReceivedAt);
        Assert.Equal(new DateTime(2026, 5, 27, 15, 0, 0), data.NextHearingAt);
        Assert.Equal("зал 12", data.NextHearingPlace);
        Assert.Contains(data.CourtActs, item => item.Title == "Решение суда" && item.ActDate == new DateTime(2026, 6, 1));
        Assert.Contains(data.MovementEvents, item => item.Title == "Судебное заседание" && item.ScheduledAt == new DateTime(2026, 5, 27, 15, 0, 0));
        Assert.Contains(data.Participants, item => item.Role == "АДМИНИСТРАТИВНЫЙ ИСТЕЦ" && item.Name == "Шафорост Антон Евгеньевич");
    }

    [Fact]
    public void ParseCaseHtml_ResolvesNextHearingFromProvidedUtcDate()
    {
        var html = BuildCourtHtml().Replace(
            "<tr><td>Судебное заседание</td><td>27.05.2026</td><td>15:00</td><td>зал 12</td><td></td><td></td><td>21.04.2026</td></tr>",
            """
               <tr><td>Судебное заседание</td><td>27.05.2026</td><td>15:00</td><td>зал 12</td><td></td><td></td><td>21.04.2026</td></tr>
               <tr><td>Судебное заседание</td><td>28.05.2026</td><td>09:30</td><td>зал 9</td><td></td><td></td><td>21.04.2026</td></tr>
            """);

        var data = SudrfCourtSyncService.ParseCaseHtml(
            html,
            new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"),
            new DateTime(2026, 5, 27, 23, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 5, 27, 15, 0, 0), data.NextHearingAt);
        Assert.Equal("зал 12", data.NextHearingPlace);
    }

    [Fact]
    public void ParseCaseHtml_ExtractsLiveSudrfIdBlocks()
    {
        var data = SudrfCourtSyncService.ParseCaseHtml(
            BuildLiveStyleCourtHtml(),
            new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"));

        Assert.Equal("Хабаровский краевой суд", data.CourtName);
        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.Equal("Казарикова Оксана Витальевна", data.JudgeName);
        Assert.Equal(new DateTime(2026, 4, 16), data.ReceivedAt);
        Assert.Equal("Краснофлотский районный суд г. Хабаровска", data.LowerCourtName);
        Assert.Equal("2а-311/2026 (2а-2448/2025;) ~ М-1858/2025", data.LowerCourtCaseNumber);
        Assert.Equal(new DateTime(2026, 5, 27, 15, 0, 0), data.NextHearingAt);
        Assert.Equal(2, data.MovementEvents.Count);
        Assert.Equal(6, data.Participants.Count);
        Assert.Contains(data.Participants, item => item.Role == "АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК" && item.Name.StartsWith("ОСП по Краснофлотскому району", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchCaseAsync_DecodesWindows1251Response()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("windows-1251").GetBytes(BuildCourtHtml());
        using var httpClient = new HttpClient(new FixedResponseHandler(_ => bytes))
        {
            BaseAddress = new Uri("https://kraevoy--hbr.sudrf.ru"),
        };
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var data = await service.FetchCaseAsync("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42", CancellationToken.None);

        Assert.Equal("Хабаровский краевой суд", data.CourtName);
        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.Equal("Шафорост Виктория Васильевна", data.Participants.Single(item => item.Role == "ЗАИНТЕРЕСОВАННОЕ ЛИЦО").Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task FetchCaseAsync_MapsExternalHttpErrorsToSudrfException(HttpStatusCode statusCode, string expectedMessagePart)
    {
        using var httpClient = new HttpClient(new StatusCodeResponseHandler(statusCode));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var error = await Assert.ThrowsAsync<SudrfCourtSyncException>(() =>
            service.FetchCaseAsync("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42", CancellationToken.None));

        Assert.Contains(expectedMessagePart, error.Message);
    }

    [Fact]
    public async Task FetchCaseAsync_MapsExternalTimeoutToSudrfException()
    {
        using var httpClient = new HttpClient(new ThrowingResponseHandler(_ => throw new TaskCanceledException("SUDRF timeout.")));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var error = await Assert.ThrowsAsync<SudrfCourtSyncException>(() =>
            service.FetchCaseAsync("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42", CancellationToken.None));

        Assert.Contains("не ответил", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchCaseAsync_PreservesCallerCancellation()
    {
        using var httpClient = new HttpClient(new ThrowingResponseHandler(_ => throw new TaskCanceledException("Caller cancelled.")));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.FetchCaseAsync("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42", cts.Token));
    }

    [Fact]
    public async Task FetchCaseAsync_SearchesCardByCaseNumberWhenSourceIsSearchPage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var handler = new RecordingResponseHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return BuildSearchResultHtml();
            }

            return request.RequestUri?.Query.Contains("name_op=case", StringComparison.OrdinalIgnoreCase) == true
                ? BuildCourtHtml()
                : BuildSearchPageHtml();
        });
        using var httpClient = new HttpClient(handler);
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var data = await service.FetchCaseAsync(
            "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=sf&delo_id=42",
            CancellationToken.None,
            "33а-3390/2026");

        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.Equal(new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"), handler.Requests.Last().RequestUri);
        var searchRequest = Assert.Single(handler.Requests, item => item.Method == HttpMethod.Post);
        Assert.Contains("p2_case__CASE_NUMBERSS=33", searchRequest.Body, StringComparison.Ordinal);
        Assert.Contains("%D0%B0-3390%2F2026", searchRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchCaseAsync_OpensCaseLinkFromJudicialUidResultWithoutCaseNumber()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var handler = new RecordingResponseHandler(request =>
            request.RequestUri?.Query.Contains("name_op=case", StringComparison.OrdinalIgnoreCase) == true
                ? BuildCourtHtml()
                : BuildJudicialUidResultHtml());
        using var httpClient = new HttpClient(handler);
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var data = await service.FetchCaseAsync(
            "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&name_op=r_juid&judicial_uid=27RS0005-01-2025-002659-56&delo_id=42",
            CancellationToken.None);

        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.DoesNotContain(handler.Requests, item => item.Method == HttpMethod.Post);
        Assert.Equal(new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"), handler.Requests.Last().RequestUri);
    }

    [Fact]
    public async Task FetchCaseAsync_SearchesByJudicialUidWhenCourtNumberContainsUid()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var handler = new RecordingResponseHandler(request =>
        {
            if (request.RequestUri?.Query.Contains("name_op=case", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BuildCourtHtml();
            }

            if (request.RequestUri?.Query.Contains("name_op=r_juid", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BuildJudicialUidResultHtml();
            }

            return BuildSearchPageHtml();
        });
        using var httpClient = new HttpClient(handler);
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        var data = await service.FetchCaseAsync(
            "https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=sf",
            CancellationToken.None,
            "27RS0005-01-2025-002659-56");

        Assert.Equal("33а-3390/2026", data.CaseNumber);
        Assert.DoesNotContain(handler.Requests, item => item.Method == HttpMethod.Post);
        Assert.Contains(handler.Requests, item =>
            item.RequestUri?.Query.Contains("name_op=r_juid", StringComparison.OrdinalIgnoreCase) == true
            && item.RequestUri.Query.Contains("judicial_uid=27RS0005-01-2025-002659-56", StringComparison.Ordinal));
        Assert.Equal(new Uri("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo&srv_num=1&name_op=case&case_id=43326936&case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&delo_id=42"), handler.Requests.Last().RequestUri);
    }

    [Theory]
    [InlineData("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo")]
    [InlineData("http://example.sudrf.ru/modules.php?name=sud_delo")]
    public void CanHandle_AcceptsSudrfHosts(string sourceUrl)
    {
        using var httpClient = new HttpClient(new FixedResponseHandler(_ => []));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        Assert.True(service.CanHandle(sourceUrl));
    }

    [Theory]
    [InlineData("https://kad.arbitr.ru/card/123")]
    [InlineData("file:///tmp/case.html")]
    [InlineData("")]
    public void CanHandle_RejectsUnsupportedSources(string sourceUrl)
    {
        using var httpClient = new HttpClient(new FixedResponseHandler(_ => []));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        Assert.False(service.CanHandle(sourceUrl));
    }

    private static string BuildCourtHtml()
        => """
           <html>
           <head>
             <meta http-equiv="Content-Type" content="text/html; charset=windows-1251">
             <title>Хабаровский краевой суд - Судебное делопроизводство</title>
           </head>
           <body>
             <h1>ДЕЛО № 33а-3390/2026</h1>
             <a href="/modules.php?name=sud_delo&amp;name_op=r_juid&amp;judicial_uid=27RS0005-01-2025-002659-56">УИД</a>
             <table class="cont1">
               <tr><th>Дата поступления</th><td>16.04.2026</td></tr>
               <tr><th>Категория дела</th><td>об оспаривании действий судебного пристава-исполнителя</td></tr>
               <tr><th>Судья</th><td>Казарикова Оксана Витальевна</td></tr>
             </table>
             <table class="cont2">
               <tr><th>Суд первой инстанции</th><td>Краснофлотский районный суд г. Хабаровска</td></tr>
               <tr><th>Номер дела в суде нижестоящей инстанции</th><td>2а-311/2026</td></tr>
             </table>
             <table class="cont3">
               <tr><th>Наименование события</th><th>Дата</th><th>Время</th><th>Место</th><th>Результат события</th><th>Основания</th><th>Дата размещения</th></tr>
               <tr><td>Передача дела судье</td><td>16.04.2026</td><td>17:01</td><td></td><td></td><td></td><td>16.04.2026</td></tr>
               <tr><td>Судебное заседание</td><td>27.05.2026</td><td>15:00</td><td>зал 12</td><td></td><td></td><td>21.04.2026</td></tr>
               <tr><td>Вынесено решение</td><td>01.06.2026</td><td></td><td></td><td>Решение удовлетворено частично</td><td></td><td>02.06.2026</td></tr>
             </table>
             <table class="cont4">
               <tr><th>Лицо</th><th>ФИО / наименование</th><th>Данные</th></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ИСТЕЦ</td><td>Шафорост Антон Евгеньевич</td><td></td></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК</td><td>Главное управление ФССП по Хабаровскому краю и ЕАО</td><td></td></tr>
               <tr><td>ПРЕДСТАВИТЕЛЬ</td><td>Кожеваткин Андрей Владимирович</td><td></td></tr>
               <tr><td>ЗАИНТЕРЕСОВАННОЕ ЛИЦО</td><td>Шафорост Виктория Васильевна</td><td></td></tr>
             </table>
           </body>
           </html>
           """;

    private static string BuildLiveStyleCourtHtml()
        => """
           <html>
           <head><meta http-equiv="Content-Type" content="text/html; charset=windows-1251"><title>Хабаровский краевой суд</title></head>
           <body>
             <div align="center" class=casenumber>ДЕЛО № 33а-3390/2026</div>
             <div id="cont1">
               <table id="tablcont">
                 <tr><th colspan="2">ДЕЛО</th></tr>
                 <tr><td><b>Уникальный идентификатор дела</b></td><td><a href="/modules.php?name=sud_delo&amp;name_op=r_juid&amp;judicial_uid=27RS0005-01-2025-002659-56">27RS0005-01-2025-002659-56</a></td></tr>
                 <tr><td><b>Дата поступления</b></td><td>16.04.2026</td></tr>
                 <tr><td><b>Категория дела</b></td><td>Об оспаривании решений</td></tr>
                 <tr><td><b>Судья</b></td><td>Казарикова Оксана Витальевна</td></tr>
               </table>
             </div>
             <div id="cont2">
               <table id="tablcont">
                 <tr><th colspan="2">РАССМОТРЕНИЕ В НИЖЕСТОЯЩЕМ СУДЕ</th></tr>
                 <tr><td><b>Суд (судебный участок) первой инстанции</b></td><td>Краснофлотский районный суд г. Хабаровска</td></tr>
                 <tr><td><b>Номер дела в первой инстанции</b></td><td>2а-311/2026 (2а-2448/2025;) ~ М-1858/2025</td></tr>
               </table>
             </div>
             <div id='cont3'><table id='tablcont'>
               <tr><th colspan=10>ДВИЖЕНИЕ ДЕЛА</th></tr>
               <tr><td><b>Наименование события</b></td><td><b>Дата</b></td><td><b>Время</b></td><td><b>Место проведения</b></td><td><b>Результат события</b></td><td><b>Основание для выбранного результата события</b></td><td><b>Примечание</b></td><td><b>Дата размещения</b></td></tr>
               <tr><td>Передача дела судье</td><td>16.04.2026</td><td>17:01</td><td></td><td></td><td></td><td></td><td>16.04.2026</td></tr>
               <tr><td>Судебное заседание</td><td>27.05.2026</td><td>15:00</td><td></td><td></td><td></td><td></td><td>21.04.2026</td></tr>
             </table></div>
             <div id='cont4'><table id='tablcont'>
               <tr><th colspan=10>УЧАСТНИКИ</th></tr>
               <tr><td><b>Вид лица, участвующего в деле</b></td><td><b>Фамилия / наименование</b></td><td><b>ИНН</b></td><td><b>КПП</b></td><td><b>ОГРН</b></td><td><b>ОГРНИП</b></td></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК</td><td>Главное управление ФССП по Хабаровскому краю и ЕАО</td><td></td><td></td><td></td><td></td></tr>
               <tr><td>ПРЕДСТАВИТЕЛЬ</td><td>Кожеваткин Андрей Владимирович</td><td></td><td></td><td></td><td></td></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК</td><td>ОСП по Краснофлотскому району г. Хабаровска ГУ ФССП России по Хабаровскому краю и Еврейской автономной области</td><td>2721121647</td><td></td><td>1042700170127</td><td></td></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ОТВЕТЧИК</td><td>Поликарпова Юлия Васильевна</td><td></td><td></td><td></td><td></td></tr>
               <tr><td>АДМИНИСТРАТИВНЫЙ ИСТЕЦ</td><td>Шафорост Антон Евгеньевич</td><td></td><td></td><td></td><td></td></tr>
               <tr><td>ЗАИНТЕРЕСОВАННОЕ ЛИЦО</td><td>Шафорост Виктория Васильевна</td><td></td><td></td><td></td><td></td></tr>
             </table></div>
             <div align="right">print</div>
           </body>
           </html>
           """;

    private static string BuildSearchPageHtml()
        => """
           <html>
           <head><meta http-equiv="Content-Type" content="text/html; charset=windows-1251"></head>
           <body>
             <form method="post" action="/modules.php">
               <input name="p2_case__CASE_NUMBERSS">
             </form>
           </body>
           </html>
           """;

    private static string BuildSearchResultHtml()
        => """
           <html>
           <head><meta http-equiv="Content-Type" content="text/html; charset=windows-1251"></head>
           <body>
             <table>
               <tr>
                 <td>33а-3390/2026</td>
                 <td><a href="/modules.php?name=sud_delo&amp;srv_num=1&amp;name_op=case&amp;case_id=43326936&amp;case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&amp;delo_id=42">дело</a></td>
               </tr>
             </table>
           </body>
           </html>
           """;

    private static string BuildJudicialUidResultHtml()
        => """
           <html>
           <head><meta http-equiv="Content-Type" content="text/html; charset=windows-1251"></head>
           <body>
             <table>
               <tr>
                 <td>27RS0005-01-2025-002659-56</td>
                 <td><a href="/modules.php?name=sud_delo&amp;srv_num=1&amp;name_op=case&amp;case_id=43326936&amp;case_uid=30d46dc6-6d6c-4514-8aa6-591c3bc63a1a&amp;delo_id=42">33а-3390/2026</a></td>
               </tr>
             </table>
           </body>
           </html>
           """;

    private sealed class FixedResponseHandler(Func<HttpRequestMessage, byte[]> resolveBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(resolveBytes(request)),
            };
            response.Content.Headers.ContentType = new("text/html")
            {
                CharSet = "windows-1251",
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StatusCodeResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/html"),
            });
    }

    private sealed class ThrowingResponseHandler(Func<HttpRequestMessage, Exception> resolveException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(resolveException(request));
    }

    private sealed class RecordingResponseHandler(Func<HttpRequestMessage, string> resolveHtml) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body));

            var bytes = Encoding.GetEncoding("windows-1251").GetBytes(resolveHtml(request));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new("text/html")
            {
                CharSet = "windows-1251",
            };
            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string Body);
}
