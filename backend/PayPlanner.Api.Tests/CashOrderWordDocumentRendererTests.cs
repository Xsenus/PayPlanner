using System.Reflection;
using System.IO.Compression;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrderWordDocumentRendererTests
{
    [Fact]
    public void RenderIncoming_AcceptsCurrentPrintTemplateWithCompositeClasses()
    {
        var renderer = typeof(CashOrdersController).GetMethod(
            "BuildIncomingCashOrderPrintHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(renderer);

        var order = new CashOrder
        {
            Number = "ПКО-2026-ИСКЛЮЧИТЕЛЬНО-ДЛИННЫЙ-НОМЕР-000123",
            Type = CashOrderType.Incoming,
            Date = new DateTime(2026, 3, 31),
            Amount = 999_999_999_999.99m,
            PayerName = "Земцов Виктор Александрович",
            Basis = "Счет №21",
            Appendix = "Договор",
            CreatedByUserName = "Кожеваткин Андрей Владимирович",
        };

        var context = CreatePrintContext("ИП Кожеваткин А.В.", "Директор", "Кожеваткин А. В.", "Иванова И. И.", "Петров П. П.", "НДС 152 542 372 881,35 руб.");
        var html = Assert.IsType<string>(renderer!.Invoke(null, [order, context]));
        Assert.Contains("class=\"line-field order-received\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"amount-words order-amount\"", html, StringComparison.Ordinal);

        var document = CashOrderWordDocumentRenderer.RenderIncoming(html);

        Assert.True(document.Length > 1_000);
        Assert.Equal((byte)'P', document[0]);
        Assert.Equal((byte)'K', document[1]);

        using var archive = new ZipArchive(new MemoryStream(document), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        var documentXml = reader.ReadToEnd();
        Assert.Contains("0310002", documentXml, StringComparison.Ordinal);
        Assert.DoesNotContain("0310001", documentXml, StringComparison.Ordinal);
        Assert.Contains("ПКО-2026-ИСКЛЮЧИТЕЛЬНО-ДЛИННЫЙ-НОМЕР-000123", documentXml, StringComparison.Ordinal);
        Assert.Contains("999999999999-99", documentXml, StringComparison.Ordinal);
        Assert.Contains("НДС 152 542 372 881,35 руб.", documentXml, StringComparison.Ordinal);
        Assert.Contains("Иванова И. И.", documentXml, StringComparison.Ordinal);
        Assert.Contains("Петров П. П.", documentXml, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span", documentXml, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderOutgoing_UsesNativeSinglePageTemplateData()
    {
        var renderer = typeof(CashOrdersController).GetMethod(
            "BuildOutgoingCashOrderPrintHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(renderer);

        var order = new CashOrder
        {
            Number = "РКО-2026-ИСКЛЮЧИТЕЛЬНО-ДЛИННЫЙ-НОМЕР-000987",
            Type = CashOrderType.Outgoing,
            Date = new DateTime(2026, 12, 31),
            Amount = 888_888_888_888.88m,
            ReceiverName = "Александрова-Краснопольская Екатерина Константиновна",
            Basis = "Выдача денежных средств на оплату государственной пошлины",
            Appendix = "Заявление на выдачу средств; приказ о командировке",
            CreatedByUserName = "System Administrator",
            Status = CashOrderStatus.Cancelled,
            CancelledAt = new DateTime(2026, 8, 26, 3, 49, 0),
            CancelledReason = "Ошибка в реквизитах получателя"
        };

        var context = CreatePrintContext("ИП Кожеваткин А.В.", "Генеральный директор", "Кожеваткин А. В.", "Иванова И. И.", "Петров П. П.", "Без налога (НДС) 0,00 руб.");
        var html = Assert.IsType<string>(renderer!.Invoke(null, [order, context]));
        var document = CashOrderWordDocumentRenderer.RenderOutgoing(html);

        using var archive = new ZipArchive(new MemoryStream(document), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        var documentXml = reader.ReadToEnd();
        Assert.Contains("0310002", documentXml, StringComparison.Ordinal);
        Assert.Contains(order.Number, documentXml, StringComparison.Ordinal);
        Assert.Contains("888 888 888 888.88", documentXml, StringComparison.Ordinal);
        Assert.Contains("Документ отменён", documentXml, StringComparison.Ordinal);
        Assert.Contains("Генеральный директор", documentXml, StringComparison.Ordinal);
        Assert.Contains("Кожеваткин А. В.", documentXml, StringComparison.Ordinal);
        Assert.Contains("Иванова И. И.", documentXml, StringComparison.Ordinal);
        Assert.Contains("Петров П. П.", documentXml, StringComparison.Ordinal);
        Assert.Contains("Подпись", documentXml, StringComparison.Ordinal);
        Assert.Contains("сумма прописью", documentXml, StringComparison.Ordinal);
        Assert.DoesNotContain("w:altChunk", documentXml, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span", documentXml, StringComparison.Ordinal);
    }

    private static object CreatePrintContext(
        string organization,
        string leaderPosition,
        string leaderName,
        string chiefAccountant,
        string cashier,
        string taxText)
    {
        var type = typeof(CashOrdersController).GetNestedType("CashOrderPrintContext", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return Activator.CreateInstance(type!, organization, leaderPosition, leaderName, chiefAccountant, cashier, taxText)!;
    }
}
