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
        Assert.Contains(data.MovementEvents, item => item.Title == "Судебное заседание" && item.ScheduledAt == new DateTime(2026, 5, 27, 15, 0, 0));
        Assert.Contains(data.Participants, item => item.Role == "АДМИНИСТРАТИВНЫЙ ИСТЕЦ" && item.Name == "Шафорост Антон Евгеньевич");
    }

    [Fact]
    public async Task FetchCaseAsync_DecodesWindows1251Response()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("windows-1251").GetBytes(BuildCourtHtml());
        using var httpClient = new HttpClient(new FixedResponseHandler(bytes))
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
    [InlineData("https://kraevoy--hbr.sudrf.ru/modules.php?name=sud_delo")]
    [InlineData("http://example.sudrf.ru/modules.php?name=sud_delo")]
    public void CanHandle_AcceptsSudrfHosts(string sourceUrl)
    {
        using var httpClient = new HttpClient(new FixedResponseHandler([]));
        var service = new SudrfCourtSyncService(httpClient, NullLogger<SudrfCourtSyncService>.Instance);

        Assert.True(service.CanHandle(sourceUrl));
    }

    [Theory]
    [InlineData("https://kad.arbitr.ru/card/123")]
    [InlineData("file:///tmp/case.html")]
    [InlineData("")]
    public void CanHandle_RejectsUnsupportedSources(string sourceUrl)
    {
        using var httpClient = new HttpClient(new FixedResponseHandler([]));
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

    private sealed class FixedResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new("text/html")
            {
                CharSet = "windows-1251",
            };
            return Task.FromResult(response);
        }
    }
}
