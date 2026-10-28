using System.Reflection;
using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using PayPlanner.Api.Models;
using PayPlanner.Api.Controllers;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskPlanningFilterTextTests
{
    private static string Render(string status, string? search = null, int? assigneeUserId = null,
        int? clientCaseId = null, int? clientId = null, int? legalEntityId = null, DateTime? from = null, DateTime? to = null,
        Dictionary<string, object?>? extra = null)
    {
        var method = typeof(TasksController).GetMethod("BuildTaskPlanningReportFilterText", BindingFlags.Static | BindingFlags.NonPublic)!;
        var values = new Dictionary<string, object?>
        {
            ["status"] = status, ["type"] = null, ["priority"] = null, ["from"] = from, ["to"] = to,
            ["search"] = search, ["assigneeUserId"] = assigneeUserId, ["clientCaseId"] = clientCaseId,
            ["clientId"] = clientId, ["legalEntityId"] = legalEntityId,
            ["timeZone"] = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yakutsk"),
            ["underControl"] = null, ["authorUserId"] = null, ["participantRole"] = null,
            ["approvalScope"] = false, ["metric"] = null, ["billingStatus"] = null, ["untriaged"] = false
        };
        if (extra is not null) foreach (var (key, value) in extra) values[key] = value;
        return (string)method.Invoke(null, method.GetParameters().Select(parameter => values[parameter.Name!]).ToArray())!;
    }

    [Theory]
    [InlineData("completed", "завершённые и отменённые")]
    [InlineData("overdue", "просроченные")]
    public void FilterDescription_MatchesTheActualStatus(string status, string expected)
    {
        Assert.Contains(expected, Render(status));
    }

    [Fact]
    public void FilterDescription_IncludesAdditionalSelectionsAndExplainsConflicts()
    {
        var value = Render("all", assigneeUserId: -1, clientId: -1, legalEntityId: -1, extra: new()
        {
            ["underControl"] = true, ["authorUserId"] = 7, ["participantRole"] = "observer",
            ["approvalScope"] = true, ["metric"] = "today", ["billingStatus"] = "unbilled", ["untriaged"] = true,
        });
        foreach (var part in new[] { "исполнитель: условия несовместимы", "клиент: условия несовместимы", "организация: условия несовместимы",
            "на контроле", "автор: №7", "участие: наблюдатель", "согласования", "показатель: на сегодня", "учёт: платные задачи без счёта", "неразобранные" })
            Assert.Contains(part, value);
    }

    [Fact]
    public void FilterDescription_IsSafeAndReadableInHtmlAndWord()
    {
        var filter = Render("all", "иск <&😀\u0001>");
        var tasks = Array.Empty<TaskJournalItem>();
        var stamp = new DateTime(2026, 6, 7, 18, 0, 0, DateTimeKind.Utc);
        var htmlBuilder = typeof(TasksController).GetMethod("BuildTaskPlanningReportHtml", BindingFlags.Static | BindingFlags.NonPublic)!;
        var html = (string)htmlBuilder.Invoke(null, new object[] { tasks, filter, stamp, TimeZoneInfo.Utc })!;
        Assert.Contains("иск <&😀 >", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("иск <&😀", html);
        var wordBuilder = typeof(TasksController).GetMethod("BuildTaskPlanningWordDocument", BindingFlags.Static | BindingFlags.NonPublic)!;
        var word = (byte[])wordBuilder.Invoke(null, new object[] { tasks, filter, stamp, TimeZoneInfo.Utc })!;
        using var archive = new ZipArchive(new MemoryStream(word), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Assert.Contains("иск <&😀 >", string.Concat(xml.Descendants(w + "t").Select(element => element.Value)));
    }

    [Fact]
    public void FilterDescription_IncludesAppliedSelectionsAndLocalDateBounds()
    {
        var from = new DateTime(2026, 6, 7, 18, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 8, 16, 0, 0, DateTimeKind.Utc);
        var value = Render("active", " иск <тест> ", 17, 28, 39, 40, from, to);
        Assert.Contains("поиск: иск <тест>", value);
        Assert.Contains("исполнитель: №17", value);
        Assert.Contains("дело: №28", value);
        Assert.Contains("клиент: №39", value);
        Assert.Contains("организация: №40", value);
        Assert.Contains("08.06.2026 03:00", value);
        Assert.Contains("09.06.2026 01:00", value);
    }
}
