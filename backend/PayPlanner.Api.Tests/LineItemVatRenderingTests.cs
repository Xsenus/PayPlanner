using PayPlanner.Api.Models;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class LineItemVatRenderingTests
{
    private static string Plain(string html) => System.Net.WebUtility.HtmlDecode(html).Replace(" ", " ").Replace(" ", " ");

    private static string RenderAct(LegalEntity? company, params ActLineItem[] lines)
    {
        var act = new Act { Number = "A-1", Date = new DateTime(2026, 10, 9), Amount = lines.Sum(l => l.Quantity * l.Price) };
        return Plain(ActDocumentTemplateEngine.RenderHtml(
            "{{act.vat}}|{{#lineItems}}{{name}}={{vat}};{{/lineItems}}", act, lines, company, false));
    }

    private static string RenderInvoice(LegalEntity? company, params InvoiceLineItemDto[] lines)
    {
        var payment = new Payment { Account = "5", Amount = lines.Sum(l => l.Quantity * l.Price), Type = PaymentType.Income };
        return Plain(InvoiceDocumentTemplateEngine.RenderHtml(
            "{{invoice.orderVatLabel}}|{{invoice.orderVatValue}}|{{#lineItems}}{{vat}};{{/lineItems}}", payment, lines, company, false));
    }

    [Fact]
    public void Act_WithRates_ShowsPerLineRateAndTotalVat()
    {
        var html = RenderAct(null,
            new ActLineItem { Name = "Консультация", Quantity = 1, Price = 1220m, VatRate = "22" },
            new ActLineItem { Name = "Выезд", Quantity = 1, Price = 500m, VatRate = "none" });

        Assert.Contains("220,00|", html);
        Assert.Contains("Консультация=22%;", html);
        Assert.Contains("Выезд=Без НДС;", html);
    }

    [Fact]
    public void Act_AllLinesWithoutVat_PrintsNoVat_AndLegacyActKeepsZero()
    {
        var exempt = RenderAct(null, new ActLineItem { Name = "Услуга", Quantity = 1, Price = 100m, VatRate = "none" });
        var legacy = RenderAct(null, new ActLineItem { Name = "Услуга", Quantity = 1, Price = 100m });

        Assert.StartsWith("Без НДС|", exempt);
        Assert.StartsWith("0,00|", legacy);
        Assert.Contains("Услуга=—;", legacy);
    }

    [Fact]
    public void Invoice_WithRates_SumsRoundedLineVat_AndLabelsLines()
    {
        var html = RenderInvoice(null,
            new InvoiceLineItemDto { Name = "A", Quantity = 1, Price = 1220m, VatRate = "22" },
            new InvoiceLineItemDto { Name = "B", Quantity = 1, Price = 110m, VatRate = "10" });

        Assert.StartsWith("В том числе НДС:|230,00|", html);
        Assert.Contains("22% / 220,00;", html);
        Assert.Contains("10% / 10,00;", html);
    }

    [Fact]
    public void Invoice_RateOverridesSellerVatExemptFlag_AndNoRatesKeepsLegacyBehaviour()
    {
        var exemptSeller = new LegalEntity { ShortName = "ИП", VatWithoutVat = true };

        var withRate = RenderInvoice(exemptSeller, new InvoiceLineItemDto { Name = "A", Quantity = 1, Price = 122m, VatRate = "22" });
        var legacy = RenderInvoice(exemptSeller, new InvoiceLineItemDto { Name = "A", Quantity = 1, Price = 122m });

        Assert.StartsWith("В том числе НДС:|22,00|", withRate);
        Assert.StartsWith("Без налога (НДС):|-|", legacy);
        Assert.Contains("Без НДС;", legacy);
    }

    [Fact]
    public void Invoice_ZeroRateIsNotTreatedAsNoVat()
    {
        var html = RenderInvoice(null, new InvoiceLineItemDto { Name = "A", Quantity = 1, Price = 100m, VatRate = "0" });

        Assert.StartsWith("В том числе НДС:|0,00|", html);
        Assert.Contains("0% / 0,00;", html);
    }
}

public class LineItemVatPersistenceTests
{
    private sealed class NoopForms : PayPlanner.Api.Services.IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    [Fact]
    public void RequestValidation_RejectsUnknownRateCode()
    {
        var request = new PayPlanner.Api.Models.Requests.ActLineItemRequest { SourceKey = "k", Name = "x", Quantity = 1, VatRate = "18" };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.ErrorMessage == "Неизвестная ставка НДС.");
    }

    [Fact]
    public async Task ManualAct_StoresAndReturnsLineVatRate_AndCorrectionCopiesIt()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PayPlanner.Api.Data.PaymentContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PayPlanner.Api.Data.PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var client = new Client { Name = "Клиент" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = new PayPlanner.Api.Controllers.ActsController(db, new NoopForms());

        var created = await controller.Create(new PayPlanner.Api.Models.Requests.UpsertActRequest
        {
            Number = "ACT-VAT-1",
            Date = new DateTime(2026, 10, 9),
            CreationMode = ActCreationMode.Manual,
            ClientId = client.Id,
            Status = ActStatus.Created,
            LineItems =
            [
                new PayPlanner.Api.Models.Requests.ActLineItemRequest { SourceKey = "m1", Name = "Консультация", Quantity = 1, Price = 1220m, VatRate = " 22% " },
                new PayPlanner.Api.Models.Requests.ActLineItemRequest { SourceKey = "m2", Name = "Выезд", Quantity = 1, Price = 500m },
            ],
        }, CancellationToken.None);

        var dto = Assert.IsType<ActDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedResult>(created.Result).Value);
        Assert.Equal(["22", null], dto.LineItems.Select(l => l.VatRate).ToArray());
        var stored = await db.Set<ActLineItem>().AsNoTracking().OrderBy(l => l.SortOrder).Select(l => l.VatRate).ToListAsync();
        Assert.Equal(["22", null], stored.ToArray());
    }
}

public class InvoiceLineItemStorageVatTests
{
    [Fact]
    public void Normalize_KeepsKnownRateAndDropsEmpty()
    {
        var items = PayPlanner.Api.Services.Invoices.InvoiceLineItemStorage.Normalize(
        [
            new PayPlanner.Api.Models.Requests.InvoiceLineItemRequest { Name = "A", Quantity = 1, Price = 10m, VatRate = "10%" },
            new PayPlanner.Api.Models.Requests.InvoiceLineItemRequest { Name = "B", Quantity = 1, Price = 10m, VatRate = "  " },
            new PayPlanner.Api.Models.Requests.InvoiceLineItemRequest { Name = "C", Quantity = 1, Price = 10m, VatRate = "none" },
        ]);

        Assert.Equal(["10", null, "none"], items.Select(i => i.VatRate).ToArray());
    }
}
