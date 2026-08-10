using System.Reflection;
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
            Number = "10",
            Type = CashOrderType.Incoming,
            Date = new DateTime(2026, 3, 31),
            Amount = 20_000m,
            PayerName = "Земцов Виктор Александрович",
            Basis = "Счет №21",
            Appendix = "Договор",
            CreatedByUserName = "Кожеваткин Андрей Владимирович",
        };

        var html = Assert.IsType<string>(renderer!.Invoke(null, [order, "ИП Кожеваткин А.В."]));
        Assert.Contains("class=\"line-field order-received\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"amount-words order-amount\"", html, StringComparison.Ordinal);

        var document = CashOrderWordDocumentRenderer.RenderIncoming(html);

        Assert.True(document.Length > 1_000);
        Assert.Equal((byte)'P', document[0]);
        Assert.Equal((byte)'K', document[1]);
    }
}
