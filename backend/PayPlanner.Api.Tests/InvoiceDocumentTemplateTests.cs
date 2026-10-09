using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoiceDocumentTemplateTests
{
    [Fact]
    public void RenderHtml_PreservesTaskCommentLines_AndMakesCrmLinksClickable()
    {
        var payment = new Payment
        {
            Account = "TASK-LINKS",
            Date = new DateTime(2026, 9, 21),
            Amount = 15_000m,
            Type = PaymentType.Income,
            Description = "Счёт подготовлен по задачам:\n1. Задача №321 — выполнена.\nhttps://crm.elitpravo.com/tasks/321",
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.DefaultTemplateHtml,
            payment,
            [new InvoiceLineItemDto { Name = "Юридическая услуга", Quantity = 1m, Unit = "усл.", Price = 15_000m }],
            null,
            false);

        Assert.Contains("Счёт подготовлен по задачам:<br/>1. Задача №321 — выполнена.<br/>", html);
        Assert.Contains("<a href=\"https://crm.elitpravo.com/tasks/321\">https://crm.elitpravo.com/tasks/321</a>", html);
    }

    [Fact]
    public async Task DocumentTemplateHistory_StoresBaseline_Deduplicates_AndRestores()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);
        var request = new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "custom-invoice",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "custom-invoice",
                    Name = "Кастомный шаблон",
                    Html = "<html><body>{{invoice.number}}</body></html>",
                },
            ],
        };

        await controller.UpsertDocumentTemplateSettings(request, CancellationToken.None);
        await controller.UpsertDocumentTemplateSettings(request, CancellationToken.None);

        var historyAction = await controller.GetDocumentTemplateHistory(CancellationToken.None);
        var historyOk = Assert.IsType<OkObjectResult>(historyAction.Result);
        var history = Assert.IsAssignableFrom<IReadOnlyList<DocumentTemplateVersionDto>>(historyOk.Value);
        Assert.Equal(2, history.Count);
        Assert.Equal([2, 1], history.Select(item => item.Revision));

        var baseline = history.Single(item => item.Revision == 1);
        var restoreAction = await controller.RestoreDocumentTemplateVersion(baseline.Id, CancellationToken.None);
        var restoreOk = Assert.IsType<OkObjectResult>(restoreAction.Result);
        var restored = Assert.IsType<InvoiceDocumentTemplateSettingsDto>(restoreOk.Value);
        Assert.Equal(InvoiceDocumentTemplateEngine.DefaultTemplateKey, restored.ActiveTemplateKey);

        historyAction = await controller.GetDocumentTemplateHistory(CancellationToken.None);
        historyOk = Assert.IsType<OkObjectResult>(historyAction.Result);
        history = Assert.IsAssignableFrom<IReadOnlyList<DocumentTemplateVersionDto>>(historyOk.Value);
        Assert.Equal(3, history.Count);
        Assert.Equal(baseline.Id, history[0].RestoredFromVersionId);
    }

    [Fact]
    public async Task GetDocumentTemplateSettings_ReturnsDefaultTemplate_WhenSettingsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.GetDocumentTemplateSettings(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<InvoiceDocumentTemplateSettingsDto>(ok.Value);
        Assert.NotEmpty(dto.Templates);
        Assert.NotEmpty(dto.Tokens);
        Assert.Equal(dto.Templates[0].Key, dto.ActiveTemplateKey);
        Assert.False(dto.PrintWithSignatureAndSealByDefault);
        Assert.Contains("{{invoice.number}}", dto.DefaultTemplateHtml);
        Assert.Contains("{{seller.sealHtml}}", dto.DefaultTemplateHtml);
        Assert.Contains("{{buyer.foundationRowHtml}}", dto.DefaultTemplateHtml);
        Assert.Contains("{{invoice.qrCodeHtml}}", dto.DefaultTemplateHtml);
        Assert.Contains("Предприниматель", dto.DefaultTemplateHtml);
        Assert.Contains("<th class=\"sum\">Сумма</th>", dto.DefaultTemplateHtml);
        Assert.Contains("<td class=\"sum\">{{total}}</td>", dto.DefaultTemplateHtml);
        Assert.Contains(".counterparty-line", dto.DefaultTemplateHtml);
        Assert.Contains("overflow-wrap: anywhere;", dto.DefaultTemplateHtml);
        Assert.Contains(dto.Templates, template => template.Key == InvoiceDocumentTemplateEngine.OfferTemplateKey);
        Assert.Contains(dto.Templates, template => template.Key == InvoiceDocumentTemplateEngine.OrderTemplateKey);
        Assert.DoesNotContain("class=\"brand-column\" rowspan=\"2\"", dto.DefaultTemplateHtml);
    }

    [Fact]
    public void CreateResponse_ReplacesRetiredCondensedStandardTemplate_AndPreservesCustomTemplate()
    {
        const string retiredDefault = """
<style>body { font-family: 'DejaVu Sans Condensed'; } .page-content { width: 178.5mm; } .items-table .name { width: 49%; } .items-table .vat { display: none; }</style>
{{seller.signatureHtml}}{{invoice.qrCodeHtml}}
""";
        const string customHtml = "<html><body>custom-invoice-{{invoice.number}}</body></html>";
        var response = InvoiceDocumentTemplateEngine.CreateResponse(new InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
            Templates =
            [
                new InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateStorageItem
                {
                    Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                    Name = "Стандартный счет",
                    Html = retiredDefault,
                },
                new InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateStorageItem
                {
                    Key = "invoice-custom",
                    Name = "Свой счет",
                    Html = customHtml,
                },
            ],
        });

        Assert.Equal(InvoiceDocumentTemplateEngine.DefaultTemplateHtml, response.Templates.Single(item => item.Key == InvoiceDocumentTemplateEngine.DefaultTemplateKey).Html);
        Assert.Equal(customHtml, response.Templates.Single(item => item.Key == "invoice-custom").Html);
    }

    [Fact]
    public void CreateResponse_AddsSystemOfferTemplate_ToExistingSettings()
    {
        var settings = InvoiceDocumentTemplateEngine.CreateResponse(new InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
            Templates =
            [
                new InvoiceDocumentTemplateEngine.InvoiceDocumentTemplateStorageItem
                {
                    Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                    Name = "Стандартный счет",
                    Html = InvoiceDocumentTemplateEngine.DefaultTemplateHtml,
                },
            ],
        });

        var offerTemplate = Assert.Single(settings.Templates, template => template.Key == InvoiceDocumentTemplateEngine.OfferTemplateKey);
        Assert.Equal("Счет-оферта", offerTemplate.Name);
        Assert.Contains("Договор публичной оферты об оказании юридических услуг", offerTemplate.Html);
        Assert.Equal(InvoiceDocumentTemplateEngine.DefaultTemplateKey, settings.ActiveTemplateKey);
        var orderTemplate = Assert.Single(settings.Templates, template => template.Key == InvoiceDocumentTemplateEngine.OrderTemplateKey);
        Assert.Equal("Заказ", orderTemplate.Name);
        Assert.Contains("Товары (работы, услуги)", orderTemplate.Html);
    }

    [Fact]
    public void RenderHtml_RendersOrderTemplate_WithKubColumnsAndTotals()
    {
        var payment = new Payment
        {
            Type = PaymentType.Income,
            Amount = 1_500m,
            Date = new DateTime(2022, 7, 5),
            Account = "15",
            Client = new Client { Name = "Исаева Марина" },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.OrderTemplateHtml,
            payment,
            [new InvoiceLineItemDto { Name = "Подготовка искового заявления", Quantity = 1m, Unit = "усл.ед.", Price = 1_500m }],
            null,
            false);

        Assert.Contains("Заказ № 15 от 5 июля 2022 г.", html);
        Assert.Contains("Исаева Марина", html);
        Assert.Contains("Товары (работы, услуги)", html);
        Assert.Contains("Всего к оплате:", html);
        Assert.Contains("Поставщик<br/>(Исполнитель):", html);
        Assert.Contains("Покупатель<br/>(Заказчик):", html);
        Assert.Contains("<col width=\"50%\" style=\"width:50%\" />", html);
        Assert.Contains("<table class=\"totals\">", html);
        Assert.DoesNotContain("seller.sealHtml", html);
    }

    [Fact]
    public void RenderHtml_AbbreviatesIndividualEntrepreneurInOrderRequisites()
    {
        var payment = new Payment
        {
            Type = PaymentType.Income,
            Amount = 1_500m,
            Date = new DateTime(2022, 7, 5),
            Account = "15",
            Client = new Client
            {
                Name = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.OrderTemplateHtml,
            payment,
            [new InvoiceLineItemDto { Name = "Услуга", Quantity = 1m, Unit = "усл.ед.", Price = 1_500m }],
            null,
            false);

        Assert.Contains("ИП Кожеваткин А.В.", html);
    }

    [Fact]
    public async Task GetDocument_ReturnsOrderAsRealDocx()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(
            paymentId,
            "word",
            InvoiceDocumentTemplateEngine.OrderTemplateKey,
            false,
            CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", file.ContentType);
        Assert.EndsWith(".docx", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        Assert.True(file.FileContents.Length > 4);
        Assert.Equal((byte)'P', file.FileContents[0]);
        Assert.Equal((byte)'K', file.FileContents[1]);
    }

    [Fact]
    public void OrderWordRenderer_EmbedsRequestedSignatureAndSeal()
    {
        const string pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var html = $"<html><body><img class=\"signature\" src=\"data:image/png;base64,{pixel}\"><img class=\"seal\" src=\"data:image/png;base64,{pixel}\"></body></html>";

        var bytes = InvoiceOrderWordDocumentRenderer.Render(html);

        using var stream = new MemoryStream(bytes);
        using var package = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(2, package.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
    }

    [Fact]
    public void RenderHtml_RendersOfferTemplate_WithInvoiceAndOfferText()
    {
        var payment = new Payment
        {
            Account = "32",
            AccountDate = new DateTime(2026, 6, 9),
            Date = new DateTime(2026, 6, 19),
            Amount = 7_000m,
            Description = "АДМ/ Ознакомление с материалами дела",
            Type = PaymentType.Income,
            Invoice = new Invoice
            {
                Number = "32",
                Date = new DateTime(2026, 6, 9),
            },
            Client = new Client
            {
                Name = "Завгородняя Ирина Александровна",
                Phone = "+79990000000",
                Email = "client@example.com",
            },
            LegalEntity = new LegalEntity
            {
                ShortName = "ИП Завгородняя Ирина Александровна",
                FullName = "Индивидуальный предприниматель Завгородняя Ирина Александровна",
                Inn = "633003961003",
                Ogrn = "321631200095369",
                Address = "446200, Самарская обл, г Новокуйбышевск, ул. Южная, дом 49",
                TreasuryBankName = "ПОВОЛЖСКИЙ БАНК ПАО СБЕРБАНК",
                TreasuryBankBik = "043601607",
                TreasuryAccountNumber = "40802810754400070904",
                TreasuryCorrAccountNumber = "30101810200000000607",
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.OfferTemplateHtml,
            payment,
            [
                new InvoiceLineItemDto
                {
                    Name = "Юридические услуги",
                    Quantity = 1,
                    Unit = "усл.ед.",
                    Price = 7_000m,
                },
            ],
            new LegalEntity
            {
                ShortName = "ИП Кожеваткин А. В.",
                FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                Inn = "633009620345",
                Ogrn = "317631300153603",
                TaxSystem = "УСН",
                Okved = "69.10",
                Phone = "8 999 333 16 47",
                Email = "info@elitpravo.com",
                TreasuryBankName = "ООО Банк Точка",
                TreasuryBankBik = "044525104",
                TreasuryAccountNumber = "40802810414500006073",
                TreasuryCorrAccountNumber = "30101810745374525104",
            },
            includeSignatureAndSeal: false);

        Assert.Contains("Счет-договор № 32 от 9 июня 2026 г.", html);
        Assert.Contains("Предмет договора:", html);
        Assert.Contains("Договор публичной оферты об оказании юридических услуг", html);
        Assert.Contains("ОГРН/ОГРНИП № 317631300153603", html);
        Assert.Contains("Индивидуальный предприниматель Завгородняя Ирина Александровна", html);
        Assert.Contains("ИНН 633003961003", html);
        Assert.Contains("ПОВОЛЖСКИЙ БАНК ПАО СБЕРБАНК", html);
        Assert.Contains("Адреса и реквизиты сторон:", html);
        Assert.Contains("Предприниматель", html);
        Assert.Contains("Юридические услуги", html);
        Assert.Contains("offer-signature-table", html);
    }

    [Fact]
    public async Task GetDocument_RendersDefaultTemplate_WithLineItemTotalColumn()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("<th class=\"sum\">Сумма</th>", content.Content);
        Assert.Matches(new Regex("<td class=\"sum\">\\s*[^<]+\\s*</td>", RegexOptions.Singleline), content.Content);
        Assert.Matches(new Regex("на сумму\\s*[^<]+\\s*руб\\.", RegexOptions.Singleline), content.Content);
        Assert.Contains("font-family: Arial, \"DejaVu Sans\", sans-serif", content.Content);
        Assert.DoesNotContain("DejaVu Sans Condensed", content.Content);
        Assert.Contains(".items-table .name {\n      width: 48%;", content.Content);
        Assert.Contains(".items-table .qty {\n      width: 7%;", content.Content);
        Assert.Contains(".items-table .unit {\n      width: 6%;", content.Content);
        Assert.Matches(@"\.items-table \.price,\s*\.items-table \.vat,\s*\.items-table \.sum\s*\{\s*width:\s*11%;", content.Content);
    }

    [Fact]
    public async Task GetDocument_RendersOfferTemplate_WhenRequested()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(
            paymentId,
            "html",
            InvoiceDocumentTemplateEngine.OfferTemplateKey,
            null,
            CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("Счет-договор", content.Content);
        Assert.Contains("Предмет договора:", content.Content);
        Assert.Contains("Договор публичной оферты об оказании юридических услуг", content.Content);
        Assert.Contains("<th class=\"sum\">Сумма</th>", content.Content);
    }

    [Fact]
    public void RenderHtml_FormatsInvoiceDocumentDatesAsLocalCalendarDates()
    {
        var payment = new Payment
        {
            Account = "INV-LOCAL-DOC",
            AccountDate = new DateTime(2026, 6, 4, 23, 30, 0, DateTimeKind.Utc),
            Date = new DateTime(2026, 6, 10, 22, 15, 0, DateTimeKind.Utc),
            Amount = 12_000m,
            Description = "Печатная проверка дат",
            Type = PaymentType.Income,
            Invoice = new Invoice
            {
                Number = "INV-LOCAL-DOC",
                Date = new DateTime(2026, 6, 4, 23, 30, 0, DateTimeKind.Utc),
            },
            Contract = new Contract
            {
                Number = "DOG-LOCAL",
                Date = new DateTime(2026, 6, 1, 23, 45, 0, DateTimeKind.Utc),
                Title = "Договор с календарной датой",
            },
            Client = new Client
            {
                Name = "Клиент печатной формы",
                Passports =
                [
                    new ClientPassport
                    {
                        Series = "1234",
                        Number = "567890",
                        IssuedAt = new DateTime(2026, 6, 5, 21, 0, 0, DateTimeKind.Utc),
                        IssuedBy = "ОВД",
                        IsPrimary = true,
                    },
                ],
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            "{{document.title}}|{{invoice.date}}|{{invoice.dueDate}}|{{buyer.contract}}|{{buyer.passport}}|{{invoice.qrCodeHtml}}",
            payment,
            [],
            new LegalEntity
            {
                ShortName = "ООО Продавец",
                FullName = "ООО Продавец",
                Inn = "1234567890",
                Kpp = "123456789",
                TreasuryBankName = "Банк",
                TreasuryBankBik = "044525225",
                TreasuryAccountNumber = "40702810900000000001",
            },
            includeSignatureAndSeal: false);

        Assert.Contains("Счет № INV-LOCAL-DOC от 04.06.2026", html);
        Assert.Contains("|04.06.2026|10.06.2026|", html);
        Assert.Contains("Договор № DOG-LOCAL от 01.06.2026", html);
        Assert.Contains("выдан 05.06.2026", html);
        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.DoesNotContain("api.qrserver.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("T00:00:00Z", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDocument_RendersActiveCustomTemplate_AsHtml()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "custom-invoice",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "custom-invoice",
                        Name = "Кастомный шаблон",
                        Html = "<html><body>{{invoice.number}}|{{buyer.name}}|{{seller.fullName}}|{{#lineItems}}[{{index}} {{name}} {{total}}]{{/lineItems}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(saveAction.Result);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.NotNull(content.Content);
        Assert.Contains("25|Тестовый клиент|Организация не выбрана|[1 Юридические услуги", content.Content);
    }

    [Fact]
    public async Task GetDocument_UsesRequestedTemplateKey_WhenProvided()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "template-a",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "template-a",
                        Name = "Template A",
                        Html = "<html><body>template-a-{{invoice.number}}</body></html>",
                    },
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "template-b",
                        Name = "Template B",
                        Html = "<html><body>template-b-{{invoice.number}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(saveAction.Result);

        var result = await controller.GetDocument(paymentId, "html", " template-b ", null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("template-b-25", content.Content);
        Assert.DoesNotContain("template-a-25", content.Content);
    }

    [Fact]
    public async Task GetDocument_ReturnsPdf_WhenPdfFormatRequested()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var pdfBytes = "%PDF-test%"u8.ToArray();
        var controller = CreateController(
            db,
            isAdmin: true,
            pdfService: new FakeInvoiceDocumentPdfService(pdfBytes));

        var result = await controller.GetDocument(paymentId, "pdf", null, null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(pdfBytes, file.FileContents);
        Assert.Equal("invoice-25.pdf", file.FileDownloadName);
    }

    [Fact]
    public async Task GetDocument_DoesNotRenderSealAndSignature_WhenDefaultPrintModeDisabled()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.DoesNotContain("data:image/png;base64,seal-demo", content.Content);
        Assert.DoesNotContain("data:image/png;base64,signature-demo", content.Content);
    }

    [Fact]
    public async Task GetDocument_RendersSealAndSignature_WhenExplicitlyRequested()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("data:image/png;base64,seal-demo", content.Content);
        Assert.Contains("data:image/png;base64,signature-demo", content.Content);

        var orderResult = await controller.GetDocument(
            paymentId,
            "html",
            InvoiceDocumentTemplateEngine.OrderTemplateKey,
            true,
            CancellationToken.None);
        var orderContent = Assert.IsType<ContentResult>(orderResult);
        Assert.NotNull(orderContent.Content);
        Assert.Contains("data:image/png;base64,seal-demo", orderContent.Content);
        Assert.Contains("data:image/png;base64,signature-demo", orderContent.Content);
    }

    [Fact]
    public async Task GetDocument_RendersKubStyleSignatureBlock_WithChiefAccountant()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("Предприниматель", content.Content);
        Assert.Contains("Кожеваткин", content.Content);
        Assert.Contains("class=\"logo\" width=\"120\" height=\"90\"", content.Content);
        Assert.Contains("class=\"invoice-qr\" width=\"150\" height=\"150\"", content.Content);
        Assert.Contains("class=\"signature\" width=\"76\" height=\"42\"", content.Content);
        Assert.Contains("class=\"seal\" width=\"90\" height=\"90\"", content.Content);
        Assert.Contains(">подпись</td>", content.Content);
        Assert.Contains(">расшифровка подписи</td>", content.Content);
    }

    [Fact]
    public async Task GetDocument_RendersExtendedBuyerBlock_WhenContractAndCaseAreLinked()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("ФИО: Тестовый клиент", content.Content);
        Assert.Contains("Адрес: Самара, ул. Тестовая, д. 1", content.Content);
        Assert.Contains("Паспорт: 1234 № 567890", content.Content);
        Assert.Contains("СНИЛС: 123-456-789 00", content.Content);
        Assert.Contains("ИНН: 123456789012", content.Content);
        Assert.Contains("Телефон: +7 999 123-45-67", content.Content);
        Assert.Contains(">Основание:</td>", content.Content);
        Assert.Contains("Договор № DOG-42 от 15.03.2026 Договор на сопровождение", content.Content);
        Assert.Contains("Комментарий для клиента:</strong> Тестовый счет", content.Content);
        Assert.Contains("Срок оплаты:</strong> 12.04.2026", content.Content);
        Assert.Contains("Счет не для бухгалтерии", content.Content);
        Assert.Contains("class=\"logo-wrap\" style=\"width: 40%; vertical-align: middle;\"", content.Content);
        Assert.Contains("<td class=\"invoice-qr-cell\" style=\"width: 150px; vertical-align: top;\">", content.Content);
        Assert.DoesNotContain("class=\"brand-column\" rowspan=\"2\"", content.Content);
        Assert.DoesNotContain("Дело:", content.Content);
        Assert.DoesNotContain("Email:", content.Content);

        var buyerNameIndex = content.Content.IndexOf("ФИО: Тестовый клиент", StringComparison.Ordinal);
        var addressIndex = content.Content.IndexOf("Адрес: Самара, ул. Тестовая, д. 1", StringComparison.Ordinal);
        var passportIndex = content.Content.IndexOf("Паспорт: 1234 № 567890", StringComparison.Ordinal);
        var snilsIndex = content.Content.IndexOf("СНИЛС: 123-456-789 00", StringComparison.Ordinal);
        var innIndex = content.Content.IndexOf("ИНН: 123456789012", StringComparison.Ordinal);
        var phoneIndex = content.Content.IndexOf("Телефон: +7 999 123-45-67", StringComparison.Ordinal);
        Assert.True(buyerNameIndex >= 0, "Buyer name should be present in the rendered document.");
        Assert.True(addressIndex > buyerNameIndex, "Address should be rendered after the buyer name.");
        Assert.True(passportIndex > addressIndex, "Passport details should be rendered after the address.");
        Assert.True(snilsIndex > passportIndex, "СНИЛС should be rendered after passport details.");
        Assert.True(innIndex > snilsIndex, "ИНН should be rendered after СНИЛС.");
        Assert.True(phoneIndex > innIndex, "Phone should be rendered after ИНН.");
    }

    [Fact]
    public async Task GetDocument_RendersBuyerRequisitesAsStableCounterpartyLines()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("<div class=\"counterparty-line\">ФИО: Тестовый клиент</div>", content.Content);
        Assert.Contains("<div class=\"counterparty-line\">Адрес: Самара, ул. Тестовая, д. 1</div>", content.Content);
        Assert.Contains("<div class=\"counterparty-line\">Паспорт: 1234 № 567890", content.Content);
        Assert.Contains("<div class=\"counterparty-line\">СНИЛС: 123-456-789 00</div>", content.Content);
        Assert.Contains("<div class=\"counterparty-line\">ИНН: 123456789012</div>", content.Content);
        Assert.Contains("<div class=\"counterparty-line\">Телефон: +7 999 123-45-67</div>", content.Content);
    }

    [Fact]
    public void RenderHtml_UsesExtendedClientProfile_WhenCanonicalBuyerFieldsAreEmpty()
    {
        var payment = new Payment
        {
            Account = "profile-only",
            Date = new DateTime(2026, 8, 23),
            Amount = 5_000m,
            Type = PaymentType.Income,
            Client = new Client
            {
                Notes = """__PP_CLIENT_PROFILE_V2__:{"lastName":"Петрова","firstName":"Анна","middleName":"Игоревна","inn":"632100000001","snils":"111-222-333 44","phones":["+7 927 000-11-22"],"emails":["anna@example.test"],"addressEntries":[{"isPrimary":true,"fullAddress":"446200, Самарская область, г. Новокуйбышевск"}],"passportEntries":[{"series":"3616","number":"123456","issuedBy":"ГУ МВД России","issueDate":"2021-05-10T00:00:00","departmentCode":"630-001"}]}""",
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.DefaultTemplateHtml,
            payment,
            [new InvoiceLineItemDto { Name = "Консультация", Quantity = 1m, Unit = "усл.", Price = 5_000m }],
            null,
            false);

        Assert.Contains("<div class=\"counterparty-line\">ФИО: Петрова Анна Игоревна</div>", html);
        Assert.Contains("<div class=\"counterparty-line\">Адрес: 446200, Самарская область, г. Новокуйбышевск</div>", html);
        Assert.Contains("<div class=\"counterparty-line\">Паспорт: 3616 № 123456", html);
        Assert.Contains("<div class=\"counterparty-line\">СНИЛС: 111-222-333 44</div>", html);
        Assert.Contains("<div class=\"counterparty-line\">ИНН: 632100000001</div>", html);
        Assert.Contains("<div class=\"counterparty-line\">Телефон: +7 927 000-11-22</div>", html);
    }

    [Fact]
    public void RenderHtml_RendersLegalEntityBuyerRequisitesAndFoundation()
    {
        var legalEntity = new LegalEntity
        {
            ShortName = "ООО Ромашка",
            FullName = "Общество с ограниченной ответственностью \"Ромашка\"",
            Inn = "6311000000",
            Kpp = "631101001",
            Ogrn = "1236300000000",
            Phone = "+7 846 200-00-00",
            Email = "office@example.test",
            Addresses =
            [
                new AddressRecord
                {
                    Type = AddressType.Legal,
                    Value = "Самара, ул. Юридическая, д. 5",
                    IsPrimary = true,
                },
            ],
        };
        var payment = new Payment
        {
            Account = "77",
            AccountDate = new DateTime(2026, 4, 10),
            Date = new DateTime(2026, 4, 20),
            Amount = 45_000m,
            Type = PaymentType.Income,
            LegalEntity = legalEntity,
            Contract = new Contract
            {
                Number = "LE-9",
                Date = new DateTime(2026, 4, 5),
                Title = "Договор с юрлицом",
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml,
            payment,
            [
                new InvoiceLineItemDto
                {
                    Name = "Юридические услуги",
                    Quantity = 1m,
                    Unit = "усл. ед.",
                    Price = 45_000m,
                },
            ],
            mainCompany: null,
            includeSignatureAndSeal: false);

        Assert.Contains("Общество с ограниченной ответственностью &quot;Ромашка&quot;, ИНН 6311000000, КПП 631101001, ОГРН 1236300000000", html);
        Assert.Contains("<div class=\"counterparty-line\">Самара, ул. Юридическая, д. 5</div>", html);
        Assert.Contains(">Основание:</td>", html);
        Assert.Contains("Договор № LE-9 от 05.04.2026 Договор с юрлицом", html);
    }

    [Fact]
    public void RenderHtml_KeepsLongCounterpartyNamesPrintableInDefaultTemplate()
    {
        var sellerFullName = "Автономная некоммерческая организация Центр защиты прав потребителей и комплексного сопровождения судебных процедур Приволжского федерального округа Самарской области";
        var buyerFullName = "Общество с ограниченной ответственностью Международная юридическая группа по сопровождению сложных имущественных и семейных споров";
        var longServiceName = "Комплексное юридическое сопровождение доверителя по многоэтапному судебному спору с подготовкой процессуальных документов и анализом материалов дела";
        var mainCompany = new LegalEntity
        {
            ShortName = "АНО Центр защиты прав",
            FullName = sellerFullName,
            Inn = "6311999999",
            Kpp = "631101001",
            TreasuryAccountNumber = "40702810900000000001",
            TreasuryBankName = "Самарский банк сопровождения расчетов с очень длинным наименованием",
            TreasuryBankBik = "043601607",
            TreasuryCorrAccountNumber = "30101810200000000607",
            Addresses =
            [
                new AddressRecord
                {
                    Type = AddressType.Legal,
                    Value = "443000, Самарская область, город Самара, улица Очень Длинная Юридическая, дом 125, офис 804",
                    IsPrimary = true,
                },
            ],
        };
        var buyer = new LegalEntity
        {
            ShortName = "ООО Международная юридическая группа",
            FullName = buyerFullName,
            Inn = "6311000001",
            Kpp = "631101002",
            Ogrn = "1236300000001",
            TreasuryAccountNumber = "40702810000000000002",
            TreasuryBankName = "Банк клиента с длинным наименованием для проверки печатной формы счета",
            TreasuryBankBik = "043601608",
            TreasuryCorrAccountNumber = "30101810200000000608",
            Addresses =
            [
                new AddressRecord
                {
                    Type = AddressType.Legal,
                    Value = "443001, Самарская область, город Самара, проспект Длинных Наименований, дом 17, помещение 240",
                    IsPrimary = true,
                },
            ],
        };
        var payment = new Payment
        {
            Account = "128",
            AccountDate = new DateTime(2026, 6, 19),
            Date = new DateTime(2026, 6, 25),
            Amount = 95_000m,
            Type = PaymentType.Income,
            LegalEntity = buyer,
            Contract = new Contract
            {
                Number = "LONG-1",
                Date = new DateTime(2026, 6, 1),
                Title = "Договор на сопровождение",
            },
        };

        var html = InvoiceDocumentTemplateEngine.RenderHtml(
            InvoiceDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml,
            payment,
            [
                new InvoiceLineItemDto
                {
                    Name = longServiceName,
                    Quantity = 1m,
                    Unit = "усл. ед.",
                    Price = 95_000m,
                },
            ],
            mainCompany,
            includeSignatureAndSeal: false);

        Assert.Contains(sellerFullName, html);
        Assert.Contains(buyerFullName, html);
        Assert.Contains(longServiceName, html);
        Assert.Contains("<div class=\"counterparty-line\">", html);
        Assert.Matches(@"\.counterparty-table\s*\{\s*table-layout:\s*fixed;", html);
        Assert.Matches(@"\.counterparty-value\s*\{[^}]*overflow-wrap:\s*anywhere;", html);
        Assert.Matches(@"\.items-table\s*\{[^}]*table-layout:\s*fixed;", html);
        Assert.Matches(@"\.items-table \.name\s*\{[^}]*overflow-wrap:\s*anywhere;", html);
        Assert.DoesNotContain("{{seller.requisites}}", html);
        Assert.DoesNotContain("{{buyer.requisitesHtml}}", html);
        Assert.DoesNotContain("{{name}}", html);
    }

    [Fact]
    public async Task GetDocument_RendersInstallmentSchedule_WhenInstallmentsExist()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateInstallmentScheduleFixtureAsync(db, paymentId);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("installment-schedule", content.Content);
        Assert.Contains("installment-title", content.Content);
        Assert.Matches(new Regex("15\\.06\\.2026\\s*-\\s*4[\\s\\u00A0]000", RegexOptions.Singleline), content.Content);
        Assert.Matches(new Regex("15\\.07\\.2026\\s*-\\s*7[\\s\\u00A0]000", RegexOptions.Singleline), content.Content);
    }

    [Fact]
    public async Task GetDocument_IncludesConvertedLegacyRepresentative_InScheduleAndTotal()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateConvertedLegacyInstallmentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Matches(new Regex("212(?:[\\s\\u00A0\\u202F]|&#160;)500,00", RegexOptions.Singleline), content.Content);
        Assert.Matches(new Regex("07\\.10\\.2025\\s*-\\s*62(?:[\\s\\u00A0\\u202F]|&#160;)500", RegexOptions.Singleline), content.Content);
    }

    [Fact]
    public async Task GetDocument_UpgradesLegacyManagedStandardTemplateLayout_WhenStoredTemplateMatchesBrokenDefault()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                        Name = "Стандартный счет",
                        Html = BuildLegacyBrokenManagedDefaultTemplateHtml(),
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(saveAction.Result);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("logo-wrap", content.Content);
        Assert.Contains("width: 150px", content.Content);
        Assert.Contains("vertical-align: top", content.Content);
        Assert.DoesNotContain("class=\"brand-column\" rowspan=\"2\"", content.Content);
    }

    [Fact]
    public async Task GetDocument_PreservesCustomizedStandardTemplate_WhenStoredHtmlDiffersFromManagedVersions()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                        Name = "Стандартный счет",
                        Html = "<html><body>custom-standard-{{invoice.number}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(saveAction.Result);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("custom-standard-25", content.Content);
    }

    [Fact]
    public async Task GetDocument_UsesDefaultMainCompanyFromNewSettingsStorage()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);

        var company = new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "Общество с ограниченной ответственностью Тест",
            Inn = "7700000000",
            Address = "Москва, тестовый адрес",
            SealDataUrl = "data:image/png;base64,new-seal",
            DirectorSignatureDataUrl = "data:image/png;base64,new-signature",
            DocumentDirectorJson = "{\"position\":\"Директор\",\"lastName\":\"Тестов\",\"firstName\":\"Иван\",\"middleName\":\"Иванович\",\"noMiddleName\":false}",
        };

        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.AddressRecords.Add(new AddressRecord
        {
            LegalEntityId = company.Id,
            Type = AddressType.Legal,
            Value = "г. Москва, ул. Ильинка, д. 9",
            UnrestrictedValue = "109012, Россия, г. Москва, ул. Ильинка, д. 9",
            IsPrimary = true,
        });
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[{company.Id}]",
        });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var result = await controller.GetDocument(paymentId, "html", null, true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("data:image/png;base64,new-seal", content.Content);
        Assert.Contains("data:image/png;base64,new-signature", content.Content);
        Assert.Contains("109012, Россия, г. Москва, ул. Ильинка, д. 9", content.Content);
        Assert.DoesNotContain("Москва, тестовый адрес", content.Content);
    }

    [Fact]
    public async Task GetDocument_UsesMainCompanyIdsFallback_WhenDefaultIdIsMissing()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);

        var company = new LegalEntity
        {
            ShortName = "Fallback Company",
            FullName = "Fallback Company Full Name",
            Inn = "7700000001",
            Address = "Fallback address",
            SealDataUrl = "data:image/png;base64,fallback-seal",
            DirectorSignatureDataUrl = "data:image/png;base64,fallback-signature",
            DocumentDirectorJson = "{\"position\":\"Director\",\"lastName\":\"Fallback\",\"firstName\":\"Ivan\",\"middleName\":\"Petrovich\",\"noMiddleName\":false}",
        };

        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[{company.Id}]",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var result = await controller.GetDocument(paymentId, "html", null, true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("data:image/png;base64,fallback-seal", content.Content);
        Assert.Contains("data:image/png;base64,fallback-signature", content.Content);
    }

    [Fact]
    public async Task GetDocument_IgnoresBrokenMainCompanyIdsJson_WhenNoFallbackExists()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = "{broken-json",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var result = await controller.GetDocument(paymentId, "html", null, true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.DoesNotContain("fallback-seal", content.Content);
        Assert.DoesNotContain("fallback-signature", content.Content);
    }

    [Fact]
    public async Task GetDocument_PrefersPrimaryBankAccount_FromCompanyBankAccountsJson()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);

        var company = new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "Общество с ограниченной ответственностью Тест",
            Inn = "7700000000",
            Address = "Москва, тестовый адрес",
            BankAccountsJson =
                "[" +
                "{\"bank\":\"Банк 1\",\"bik\":\"111111111\",\"accountNumber\":\"40802810000000000001\",\"accountType\":\"Дополнительный\"}," +
                "{\"bank\":\"Банк 2\",\"bik\":\"222222222\",\"accountNumber\":\"40802810000000000002\",\"accountType\":\"Основной\",\"corrAccountNumber\":\"30101810000000000002\"}" +
                "]",
        };

        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[{company.Id}]",
        });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("40802810000000000002", content.Content);
        Assert.Contains("222222222", content.Content);
        Assert.DoesNotContain("40802810000000000001", content.Content);
    }

    [Fact]
    public async Task GetDocument_UsesBuyerProfilePassportFallback_WhenNormalizedPassportMissing()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db, createNormalizedPassport: false);
        var controller = CreateController(db, isAdmin: true);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("Паспорт: 1234 № 567890", content.Content);
        Assert.Contains("код подразделения 123-456", content.Content);
        Assert.Contains("ИНН: 123456789012", content.Content);
        Assert.Contains("СНИЛС: 123-456-789 00", content.Content);
        Assert.Contains(">Основание:</td>", content.Content);
        Assert.Contains("Договор № DOG-42 от 15.03.2026 Договор на сопровождение", content.Content);
        Assert.DoesNotContain("Дело:", content.Content);

        var buyerNameIndex = content.Content.IndexOf("ФИО: Тестовый клиент", StringComparison.Ordinal);
        var addressIndex = content.Content.IndexOf("Адрес: Самара, ул. Тестовая, д. 1", StringComparison.Ordinal);
        var passportIndex = content.Content.IndexOf("Паспорт: 1234 № 567890", StringComparison.Ordinal);
        var snilsIndex = content.Content.IndexOf("СНИЛС: 123-456-789 00", StringComparison.Ordinal);
        var innIndex = content.Content.IndexOf("ИНН: 123456789012", StringComparison.Ordinal);
        var phoneIndex = content.Content.IndexOf("Телефон: +7 999 123-45-67", StringComparison.Ordinal);
        Assert.True(buyerNameIndex >= 0, "Buyer name should be present in the rendered document.");
        Assert.True(addressIndex > buyerNameIndex, "Address should be rendered after the buyer name.");
        Assert.True(passportIndex > addressIndex, "Passport details should be rendered after the address.");
        Assert.True(snilsIndex > passportIndex, "СНИЛС should be rendered after passport details.");
        Assert.True(innIndex > snilsIndex, "ИНН should be rendered after СНИЛС.");
        Assert.True(phoneIndex > innIndex, "Phone should be rendered after ИНН.");
    }

    [Fact]
    public async Task GetDocument_RendersCorrAndSettlementAccount_InExpectedBankGridRows()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);

        var company = new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "Общество с ограниченной ответственностью Тест",
            Inn = "7700000000",
            Address = "Москва, тестовый адрес",
            BankAccountsJson =
                "[" +
                "{\"bank\":\"Банк 1\",\"bik\":\"111111111\",\"accountNumber\":\"40802810000000000001\",\"accountType\":\"Дополнительный\"}," +
                "{\"bank\":\"Банк 2\",\"bik\":\"222222222\",\"accountNumber\":\"40802810000000000002\",\"accountType\":\"Основной\",\"corrAccountNumber\":\"30101810000000000002\"}" +
                "]",
        };

        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[{company.Id}]",
        });
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Matches(new Regex("rowspan=\"2\" class=\"v-al-t bor0-t\" style=\"width: 35%;\">30101810000000000002</td>", RegexOptions.Singleline), content.Content);
        Assert.Matches(new Regex("rowspan=\"4\" class=\"v-al-t\" style=\"width: 35%;\">40802810000000000002</td>", RegexOptions.Singleline), content.Content);
    }

    [Fact]
    public async Task GetDocument_UsesStoredPrintMode_WhenOverrideNotProvided()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                PrintWithSignatureAndSealByDefault = true,
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
                        Name = "Стандартный счет",
                        Html = InvoiceDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml,
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(saveAction.Result);

        var result = await controller.GetDocument(paymentId, "html", null, null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("data:image/png;base64,seal-demo", content.Content);
        Assert.Contains("data:image/png;base64,signature-demo", content.Content);
    }

    [Fact]
    public async Task SendInvoiceEmail_SendsPdfAttachment_WhenPdfFormatRequested()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var emailService = new RecordingEmailService();
        var controller = CreateController(
            db,
            isAdmin: true,
            emailService: emailService,
            pdfService: new FakeInvoiceDocumentPdfService("%PDF-mail%"u8.ToArray()));

        var result = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest { Format = "pdf" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal("invoice-25.pdf", emailService.AttachmentFileName);
        Assert.Equal("application/pdf", emailService.AttachmentContentType);
        Assert.Equal("%PDF-mail%"u8.ToArray(), emailService.AttachmentBytes);
    }

    [Fact]
    public async Task SendInvoiceEmail_RequiresAccountsExportPermission()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "accounts-no-export", canExport: false);
        var emailService = new RecordingEmailService();
        var controller = CreateController(db, isAdmin: false, emailService: emailService, userId: user.Id);

        var result = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest { Format = "word" },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Null(emailService.ToEmail);
    }

    [Fact]
    public async Task GetDocument_RequiresAccountsExportPermission()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "accounts-doc-no-export", canExport: false);
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        var result = await controller.GetDocument(paymentId, "html", templateKey: null, includeSignatureAndSeal: false, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetPreview_AllowsViewingWithoutExport_AndDoesNotRememberTemplate()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "preview-no-export", canExport: false);
        var payment = await db.Payments.SingleAsync(item => item.Id == paymentId);
        var originalTemplate = payment.InvoiceDocumentTemplateKey;
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        var result = Assert.IsType<ContentResult>(await controller.GetPreview(paymentId));

        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("Юридические услуги", result.Content);
        Assert.Equal(originalTemplate, payment.InvoiceDocumentTemplateKey);
        Assert.IsType<ForbidResult>(await controller.GetDocument(paymentId, "word"));
        Assert.IsType<ForbidResult>(await controller.GetDocument(paymentId, "pdf"));
    }

    [Fact]
    public async Task GetPreview_ForbidsUserWithoutViewPermission()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "preview-no-view", canExport: true);
        user.Role!.Permissions.Single().CanView = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        Assert.IsType<ForbidResult>(await controller.GetPreview(paymentId));
    }

    [Fact]
    public async Task GetPreview_DoesNotExposeInvoiceOutsideUserScope()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var current = await CreateAccountsUserAsync(db, "preview-own", canExport: false, viewScope: DataVisibilityScope.Own);
        var owner = await CreateAccountsUserAsync(db, "preview-owner", canExport: true);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == paymentId);
        payment.Client!.ResponsibleUserId = owner.Id;
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: current.Id);

        Assert.IsType<NotFoundResult>(await controller.GetPreview(paymentId));
    }

    [Theory]
    [InlineData(DocumentKind.Accrual)]
    [InlineData(DocumentKind.Payment)]
    public async Task GetPreview_DoesNotExposeStandaloneFinancialDocument(DocumentKind documentKind)
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "preview-financial", canExport: false);
        var payment = await db.Payments.SingleAsync(item => item.Id == paymentId);
        payment.InvoiceId = null;
        payment.DocumentKind = documentKind;
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        Assert.IsType<NotFoundResult>(await controller.GetPreview(paymentId));
    }

    [Fact]
    public async Task GetPreview_DoesNotExposeNonrepresentativeLinkedPayment()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "preview-linked-payment", canExport: false);
        var invoicePayment = await db.Payments.SingleAsync(item => item.Id == paymentId);
        var linkedPayment = new Payment
        {
            InvoiceId = invoicePayment.InvoiceId,
            ClientId = invoicePayment.ClientId,
            DocumentKind = DocumentKind.Payment,
            Date = invoicePayment.Date,
            Amount = 500m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
        };
        db.Payments.Add(linkedPayment);
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        Assert.IsType<NotFoundResult>(await controller.GetPreview(linkedPayment.Id));
    }

    [Fact]
    public async Task GetPreview_AllowsLegacyPaymentRepresentingVisibleInvoice()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "preview-legacy-representative", canExport: false);
        var payment = await db.Payments.SingleAsync(item => item.Id == paymentId);
        payment.DocumentKind = DocumentKind.Payment;
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: user.Id);

        var result = Assert.IsType<ContentResult>(await controller.GetPreview(paymentId));

        Assert.Contains("Юридические услуги", result.Content);
    }

    [Fact]
    public async Task GetDocument_ReturnsNotFoundForHiddenInvoice()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var current = await CreateAccountsUserAsync(db, "accounts-doc-own", canExport: true, viewScope: DataVisibilityScope.Own);
        var owner = await CreateAccountsUserAsync(db, "accounts-doc-owner", canExport: true);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == paymentId);
        payment.Client!.ResponsibleUserId = owner.Id;
        await db.SaveChangesAsync();
        var controller = CreateController(db, isAdmin: false, userId: current.Id);

        var result = await controller.GetDocument(paymentId, "html", templateKey: null, includeSignatureAndSeal: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ForbidsNonAdminUsers()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: false);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Default",
                        Html = "<html><body>template</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ForbidsAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateAccountsUserAsync(db, "invoice-template-stale-admin", canExport: true);
        var controller = CreateController(db, isAdmin: true, userId: user.Id);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Default",
                        Html = "<html><body>template</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
        Assert.Empty(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);
        controller.ModelState.AddModelError("Templates", "Required");

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Default",
                        Html = "<html><body>template</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Templates", details.Errors.Keys);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsDuplicateTemplateKeys()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "duplicate",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "duplicate",
                        Name = "Template 1",
                        Html = "<html><body>one</body></html>",
                    },
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "duplicate",
                        Name = "Template 2",
                        Html = "<html><body>two</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsEmptyTemplateCollection()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "missing",
                Templates = [],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsBlankTemplateKey()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = " ",
                        Name = "Template without key",
                        Html = "<html><body>content</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsBlankTemplateName()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = " ",
                        Html = "<html><body>content</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsBlankTemplateHtml()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "default",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Standard",
                        Html = " ",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_RejectsMissingOrUnknownActiveTemplateKey()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var missingActiveKey = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = " ",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Default",
                        Html = "<html><body>content</body></html>",
                    },
                ],
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingActiveKey.Result);

        var unknownActiveKey = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "other",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "default",
                        Name = "Default",
                        Html = "<html><body>content</body></html>",
                    },
                ],
            },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(unknownActiveKey.Result);
    }

    [Fact]
    public async Task GetDocument_ReturnsNotFound_WhenInvoiceDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.GetDocument(999, "html", null, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task GetDocument_ReturnsWordAttachment_ForUnknownFormat()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.GetDocument(paymentId, "xlsx", null, null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal(PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType, file.ContentType);
        Assert.Equal("invoice-25.docx", file.FileDownloadName);
    }

    [Fact]
    public async Task GetDocument_ReturnsProblem_WhenPdfServiceIsMissing()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.GetDocument(paymentId, "pdf", null, null, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);
        controller.ModelState.AddModelError("Email", "Invalid");

        var action = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest { Email = "broken" },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Email", details.Errors.Keys);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsNotFound_WhenInvoiceDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.SendInvoiceEmail(
            999,
            new SendInvoiceEmailRequest { Email = "mail@example.com" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task SendInvoiceEmail_RequiresRecipient_WhenRequestAndClientEmailAreEmpty()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == paymentId);
        payment.Client!.Email = "   ";
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true);
        var action = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Recipient email is required.", badRequest.Value);
    }

    [Fact]
    public async Task SendInvoiceEmail_NormalizesHtmlFormatToWord_AndUsesCustomContent()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var emailService = new RecordingEmailService();
        var controller = CreateController(db, isAdmin: true, emailService: emailService);

        var action = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest
            {
                Email = "custom@example.com",
                Subject = "Custom subject",
                Message = "Custom body",
                Format = "html",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(ok.Value);
        Assert.Equal("word", ok.Value!.GetType().GetProperty("format")!.GetValue(ok.Value));
        Assert.Equal("custom@example.com", emailService.ToEmail);
        Assert.Equal("Custom subject", emailService.Subject);
        Assert.Equal("Custom body", emailService.Body);
        Assert.Equal("invoice-25.docx", emailService.AttachmentFileName);
        Assert.Equal(PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType, emailService.AttachmentContentType);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsProblem_WhenPdfServiceIsMissing()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var controller = CreateController(db, isAdmin: true);

        var action = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest
            {
                Email = "pdf@example.com",
                Format = "pdf",
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task UploadDocumentToYandexDisk_UploadsInvoiceDocumentAndWritesAudit()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var yandexDiskService = new RecordingYandexDiskService();
        var controller = CreateController(db, isAdmin: true, yandexDiskService: yandexDiskService);

        var action = await controller.UploadDocumentToYandexDisk(
            paymentId,
            "word",
            templateKey: null,
            includeSignatureAndSeal: false,
            overwrite: true,
            CancellationToken.None);

        var result = Assert.IsType<YandexDiskOperationResultDto>(action.Value);
        Assert.True(result.Success);
        Assert.Equal("/CRM/invoice-25.docx", result.Path);
        Assert.NotNull(yandexDiskService.Upload);
        Assert.Equal("invoice", yandexDiskService.Upload.DocumentKind);
        Assert.Equal("Счет", yandexDiskService.Upload.DocumentType);
        Assert.Equal("25", yandexDiskService.Upload.DocumentNumber);
        Assert.Equal("Тестовый счет", yandexDiskService.Upload.DocumentTitle);
        Assert.Equal("Тестовый клиент", yandexDiskService.Upload.ClientName);
        Assert.Equal("invoice-25.docx", yandexDiskService.Upload.OriginalFileName);
        Assert.Equal(PayPlanner.Api.Services.Documents.HtmlWordDocumentBuilder.ContentType, yandexDiskService.Upload.ContentType);
        Assert.True(yandexDiskService.Upload.OverwriteExisting);
        Assert.NotEmpty(yandexDiskService.UploadBytes!);

        var audit = await db.ObjectAuditEvents.AsNoTracking().SingleAsync(item =>
            item.ObjectType == "payment"
            && item.ObjectId == paymentId
            && item.Action == "YandexDiskUploaded");
        Assert.Contains("отправлен на Яндекс.Диск", audit.Description);

        var syncState = await db.YandexDiskCrmSyncStates.AsNoTracking().SingleAsync(item =>
            item.DocumentKind == "invoice"
            && item.ObjectType == "payment"
            && item.ObjectId == paymentId);
        Assert.Equal(YandexDiskCrmSyncStatus.Success, syncState.Status);
        Assert.Equal("/CRM/invoice-25.docx", syncState.YandexDiskPath);
        Assert.Equal("invoice-25.docx", syncState.FileName);
        Assert.Equal(1, syncState.AttemptCount);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsNotFoundForHiddenInvoice()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var current = await CreateAccountsUserAsync(db, "accounts-mail-own", canExport: true, viewScope: DataVisibilityScope.Own);
        var owner = await CreateAccountsUserAsync(db, "accounts-mail-owner", canExport: true);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == paymentId);
        payment.Client!.ResponsibleUserId = owner.Id;
        await db.SaveChangesAsync();
        var emailService = new RecordingEmailService();
        var controller = CreateController(db, isAdmin: false, emailService: emailService, userId: current.Id);

        var result = await controller.SendInvoiceEmail(
            paymentId,
            new SendInvoiceEmailRequest { Email = "hidden@example.test", Format = "word" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null(emailService.ToEmail);
    }

    [Fact]
    public async Task UploadDocumentToYandexDisk_ReturnsNotFoundForHiddenInvoice()
    {
        await using var db = await CreateContextAsync();
        var paymentId = await CreateInvoiceDocumentFixtureAsync(db);
        var current = await CreateAccountsUserAsync(
            db,
            "accounts-yandex-own",
            canExport: true,
            viewScope: DataVisibilityScope.Own,
            canCreateYandexDisk: true);
        var owner = await CreateAccountsUserAsync(db, "accounts-yandex-owner", canExport: true);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == paymentId);
        payment.Client!.ResponsibleUserId = owner.Id;
        await db.SaveChangesAsync();
        var yandexDiskService = new RecordingYandexDiskService();
        var controller = CreateController(db, isAdmin: false, yandexDiskService: yandexDiskService, userId: current.Id);

        var result = await controller.UploadDocumentToYandexDisk(
            paymentId,
            "word",
            templateKey: null,
            includeSignatureAndSeal: false,
            overwrite: true,
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Null(yandexDiskService.Upload);
    }

    private static async Task<int> CreateInvoiceDocumentFixtureAsync(PaymentContext db, bool createNormalizedPassport = true)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Address = "Самара, ул. Тестовая, д. 1",
            Email = "client@example.com",
            Phone = "+7 999 123-45-67",
            Company = "ООО Тестовый заказчик",
            Notes = """__PP_CLIENT_PROFILE_V2__:{"inn":"123456789012","snils":"123-456-789 00","passportSeries":"1234","passportNumber":"567890","passportIssuedBy":"ОВД Тестового района","passportIssueDate":"2020-01-12T00:00:00","passportDepartmentCode":"123-456","passportEntries":[{"series":"1234","number":"567890","issuedBy":"ОВД Тестового района","issueDate":"2020-01-12T00:00:00","departmentCode":"123-456"}]}""",
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.AddressRecords.Add(new AddressRecord
        {
            ClientId = client.Id,
            Type = AddressType.Registration,
            Value = "Самара, ул. Тестовая, д. 1",
            IsPrimary = true,
        });

        if (createNormalizedPassport)
        {
            db.ClientPassports.Add(new ClientPassport
            {
                ClientId = client.Id,
                Series = "1234",
                Number = "567890",
                IssuedBy = "ОВД Тестового района",
                IssuedAt = new DateTime(2020, 1, 12),
                DepartmentCode = "123-456",
                IsPrimary = true,
            });
        }

        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 2),
        };
        db.Invoices.Add(invoice);

        var contract = new Contract
        {
            Number = "DOG-42",
            Date = new DateTime(2026, 3, 15),
            Title = "Договор на сопровождение",
            BillingMode = ContractBillingMode.Fixed,
        };
        db.Contracts.Add(contract);

        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Дело о взыскании задолженности",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = "25",
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            ContractId = contract.Id,
            ClientCaseId = clientCase.Id,
            DocumentKind = DocumentKind.Invoice,
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Amount = 11_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Description = "Тестовый счет",
            IsNotForAccounting = true,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = payment.Id,
            ClientCaseId = clientCase.Id,
        });

        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            PaymentId = payment.Id,
            Name = "Юридические услуги",
            Quantity = 1m,
            Unit = "усл. ед.",
            Price = 11_000m,
            SortOrder = 1,
        });

        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task<int> CreateConvertedLegacyInstallmentFixtureAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Converted Legacy Client",
        };
        db.Clients.Add(client);

        var invoice = new Invoice
        {
            Number = "122",
            Date = new DateTime(2025, 7, 3),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representative = new Payment
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            DocumentKind = DocumentKind.Invoice,
            Date = new DateTime(2025, 10, 7),
            PlannedDate = new DateTime(2025, 10, 7),
            Amount = 62_500m,
            PaidAmount = 62_500m,
            PaidDate = new DateTime(2025, 10, 7),
            LastPaymentDate = new DateTime(2025, 10, 7),
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Description = "Invoice 122 final legacy installment",
        };
        db.Payments.Add(representative);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            CreateConvertedLegacyInstallment(invoice, client, 1, new DateTime(2025, 7, 8), 50_000m),
            CreateConvertedLegacyInstallment(invoice, client, 2, new DateTime(2025, 8, 8), 50_000m),
            CreateConvertedLegacyInstallment(invoice, client, 3, new DateTime(2025, 9, 9), 50_000m));
        await db.SaveChangesAsync();

        return representative.Id;
    }

    private static Payment CreateConvertedLegacyInstallment(
        Invoice invoice,
        Client client,
        int sequence,
        DateTime date,
        decimal amount)
        => new()
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            DocumentKind = DocumentKind.Payment,
            Date = date,
            PlannedDate = date,
            Amount = amount,
            PaidAmount = amount,
            PaidDate = date,
            LastPaymentDate = date,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Description = $"Installment {sequence}",
            SystemNotes = FormattableString.Invariant(
                $"[pp:installment-payment] {{\"Sequence\":{sequence},\"Total\":62500,\"DownPayment\":0,\"AnnualRate\":0,\"Months\":3,\"StartDate\":\"2025-07-08T00:00:00\",\"RoundingMode\":0,\"RoundingStep\":null,\"ToPay\":62500,\"Overpay\":0,\"LoanAmount\":62500,\"BaseMonthlyPayment\":20833.33,\"RoundedMonthlyPayment\":null,\"Item\":{{\"Sequence\":{sequence},\"Date\":\"{date:yyyy-MM-dd}T00:00:00\",\"Principal\":{amount},\"Interest\":0,\"Payment\":{amount},\"Balance\":0}}}}"),
        };

    private static async Task CreateInstallmentScheduleFixtureAsync(PaymentContext db, int parentPaymentId)
    {
        var parentPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == parentPaymentId);

        db.Payments.AddRange(
            new Payment
            {
                Account = parentPayment.Account,
                AccountDate = parentPayment.AccountDate,
                InvoiceId = parentPayment.InvoiceId,
                ClientId = parentPayment.ClientId,
                ContractId = parentPayment.ContractId,
                ClientCaseId = parentPayment.ClientCaseId,
                DocumentKind = DocumentKind.Payment,
                Date = new DateTime(2026, 6, 15),
                PlannedDate = new DateTime(2026, 6, 15),
                Amount = 4_000m,
                Status = PaymentStatus.Pending,
                Type = parentPayment.Type,
                Description = "Installment 2/2",
                SystemNotes =
                    "[pp:installment-payment] {\"sequence\":2,\"total\":11000,\"downPayment\":0,\"annualRate\":0,\"months\":2,\"startDate\":\"2026-04-12T00:00:00\",\"roundingMode\":0,\"roundingStep\":0,\"toPay\":11000,\"overpay\":0,\"loanAmount\":11000,\"baseMonthlyPayment\":5500,\"roundedMonthlyPayment\":5500,\"item\":{\"sequence\":2,\"date\":\"2026-06-15T00:00:00\",\"principal\":4000,\"interest\":0,\"payment\":4000,\"balance\":0}}",
            },
            new Payment
            {
                Account = parentPayment.Account,
                AccountDate = parentPayment.AccountDate,
                InvoiceId = parentPayment.InvoiceId,
                ClientId = parentPayment.ClientId,
                ContractId = parentPayment.ContractId,
                ClientCaseId = parentPayment.ClientCaseId,
                DocumentKind = DocumentKind.Payment,
                Date = new DateTime(2026, 7, 15),
                PlannedDate = new DateTime(2026, 7, 15),
                Amount = 7_000m,
                Status = PaymentStatus.Pending,
                Type = parentPayment.Type,
                Description = "Installment fallback",
                SystemNotes = "[pp:installment-payment] broken-json",
            });

        await db.SaveChangesAsync();
    }

    private static async Task CreateMainCompanyAsync(PaymentContext db)
    {
        var company = new LegalEntity
        {
            ShortName = "ИП Кожеваткин А.В.",
            FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
            Inn = "633009620345",
            Address = "446200, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9",
            LogoDataUrl = "data:image/png;base64,logo-demo",
            SealDataUrl = "data:image/png;base64,seal-demo",
            DirectorSignatureDataUrl = "data:image/png;base64,signature-demo",
            DocumentDirectorJson = "{\"position\":\"Предприниматель\",\"lastName\":\"Кожеваткин\",\"firstName\":\"Андрей\",\"middleName\":\"Владимирович\",\"noMiddleName\":false}",
            ChiefAccountantJson = "{\"position\":\"Главный (старший) бухгалтер\",\"lastName\":\"Кожеваткин\",\"firstName\":\"Андрей\",\"middleName\":\"Владимирович\",\"noMiddleName\":false}",
            BankAccountsJson = "[{\"bank\":\"ООО \\\"Банк Точка\\\"\",\"bik\":\"044525104\",\"accountNumber\":\"40802810414500006073\"}]",
            TreasuryCorrAccountNumber = "30101810745374525104",
        };

        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateAccountsUserAsync(
        PaymentContext db,
        string name,
        bool canExport,
        string viewScope = DataVisibilityScope.All,
        bool canCreateYandexDisk = false)
    {
        var role = new Role
        {
            Name = $"{name}-{Guid.NewGuid():N}",
            Description = name,
            Permissions =
            {
                new RolePermission
                {
                    Section = RolePermissionCatalog.Accounts,
                    CanView = true,
                    CanCreate = true,
                    CanEdit = true,
                    CanDelete = true,
                    CanExport = canExport,
                    ViewScope = viewScope,
                },
            },
        };
        if (canCreateYandexDisk)
        {
            role.Permissions.Add(new RolePermission
            {
                Section = RolePermissionCatalog.YandexDisk,
                CanView = true,
                CanCreate = true,
                ViewScope = DataVisibilityScope.All,
            });
        }

        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static string BuildLegacyBrokenManagedDefaultTemplateHtml()
    {
        var currentDefaultHtml = InvoiceDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml;
        var fixedHeaderCss = """
    .logo-wrap {
      text-align: center;
    }
    .logo-wrap .logo {
      display: block;
      max-height: 90px;
      max-width: 180px;
      margin-left: auto;
      margin-right: auto;
      object-fit: contain;
    }
    .invoice-qr {
      display: block;
      width: 150px;
      height: 150px;
      max-width: 150px;
      max-height: 150px;
      margin-left: auto;
      object-fit: contain;
    }
""";
        var brokenHeaderCss = """
    .header-table {
      table-layout: fixed;
    }
    .seller-title-cell {
      padding: 2px 0 8px;
      vertical-align: middle;
    }
    .bank-cell {
      vertical-align: top;
    }
    .brand-column {
      width: 165px;
      padding-left: 10px;
      vertical-align: top;
    }
    .brand-stack {
      width: 150px;
      margin-left: auto;
    }
    .logo-wrap {
      min-height: 88px;
      padding-top: 2px;
      text-align: center;
    }
    .logo-wrap .logo {
      display: block;
      max-height: 82px;
      max-width: 150px;
      margin: 0 auto;
      object-fit: contain;
    }
    .qr-wrap {
      margin-top: 10px;
    }
    .invoice-qr {
      display: block;
      width: 150px;
      height: 150px;
      max-width: 150px;
      max-height: 150px;
      margin: 0 auto;
      object-fit: contain;
    }
""";
        var fixedHeaderHtml = """
      <table style="margin-bottom: 10px;">
        <tbody>
          <tr>
            <td class="doc-title" style="width: 60%; vertical-align: middle; padding: 2px 0;">
              {{seller.fullName}}
            </td>
            <td class="logo-wrap" style="width: 40%; vertical-align: middle;">
              {{seller.logoHtml}}
            </td>
          </tr>
        </tbody>
      </table>

      <table style="margin: 0 0 10px;">
        <tbody>
          <tr>
            <td>
              <table class="bank-grid">
                <tbody>
                  <tr>
                    <td colspan="2" rowspan="2" class="v-al-t bor0-b" style="width: 50%;">
                      {{seller.bankName}}
                    </td>
                    <td style="width: 15%;">БИК</td>
                    <td class="bor0-b" style="width: 35%;">{{seller.bik}}</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="2" class="v-al-t bor0-t" style="width: 35%;">{{seller.corrAccountNumber}}</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Банк получателя</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td style="width: 25%;">ИНН {{seller.inn}}</td>
                    <td style="width: 25%;">КПП {{seller.kppOrDash}}</td>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="4" class="v-al-t" style="width: 35%;">{{seller.accountNumber}}</td>
                  </tr>
                  <tr>
                    <td rowspan="2" colspan="2" class="v-al-t bor0-b" style="width: 50%;">{{seller.fullName}}</td>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Получатель</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                </tbody>
              </table>
            </td>
            <td style="width: 150px; vertical-align: top;">
              {{invoice.qrCodeHtml}}
            </td>
          </tr>
        </tbody>
      </table>
""";
        var brokenHeaderHtml = EncodeUtf8AsWindows1251Mojibake("""
      <table class="header-table" style="margin: 0 0 10px;">
        <tbody>
          <tr>
            <td class="doc-title seller-title-cell">
              {{seller.fullName}}
            </td>
            <td class="brand-column" rowspan="2">
              <div class="brand-stack">
                <div class="logo-wrap">
                  {{seller.logoHtml}}
                </div>
                <div class="qr-wrap">
                  {{invoice.qrCodeHtml}}
                </div>
              </div>
            </td>
          </tr>
          <tr>
            <td class="bank-cell">
              <table class="bank-grid">
                <tbody>
                  <tr>
                    <td colspan="2" rowspan="2" class="v-al-t bor0-b" style="width: 50%;">
                      {{seller.bankName}}
                    </td>
                    <td style="width: 15%;">БИК</td>
                    <td class="bor0-b" style="width: 35%;">{{seller.bik}}</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="2" class="v-al-t bor0-t" style="width: 35%;">{{seller.corrAccountNumber}}</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Банк получателя</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td style="width: 25%;">ИНН {{seller.inn}}</td>
                    <td style="width: 25%;">КПП {{seller.kppOrDash}}</td>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="4" class="v-al-t" style="width: 35%;">{{seller.accountNumber}}</td>
                  </tr>
                  <tr>
                    <td rowspan="2" colspan="2" class="v-al-t bor0-b" style="width: 50%;">{{seller.fullName}}</td>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Получатель</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </tbody>
      </table>
""");

        return currentDefaultHtml
            .Replace(fixedHeaderCss, brokenHeaderCss, StringComparison.Ordinal)
            .Replace(fixedHeaderHtml, brokenHeaderHtml, StringComparison.Ordinal);
    }

    private static string EncodeUtf8AsWindows1251Mojibake(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetString(Encoding.UTF8.GetBytes(value));
    }

    private static InvoicesController CreateController(
        PaymentContext db,
        bool isAdmin,
        IEmailService? emailService = null,
        IInvoiceDocumentPdfService? pdfService = null,
        IYandexDiskService? yandexDiskService = null,
        int? userId = null)
    {
        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
            claims.Add(new Claim(ClaimTypes.Name, "Test user"));
        }

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        var controller = new InvoicesController(
            db,
            emailService ?? new NoopEmailService(),
            new NoopFormRequirementsService(),
            pdfService,
            yandexDiskService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class FakeInvoiceDocumentPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;

        public FakeInvoiceDocumentPdfService(byte[] bytes)
        {
            _bytes = bytes;
        }

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
            => Task.FromResult(_bytes);
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public string? ToEmail { get; private set; }
        public string? Subject { get; private set; }
        public string? Body { get; private set; }
        public string? AttachmentFileName { get; private set; }
        public byte[]? AttachmentBytes { get; private set; }
        public string? AttachmentContentType { get; private set; }

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType)
        {
            ToEmail = toEmail;
            Subject = subject;
            Body = body;
            AttachmentFileName = attachmentFileName;
            AttachmentBytes = attachmentBytes;
            AttachmentContentType = attachmentContentType;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingYandexDiskService : IYandexDiskService
    {
        public YandexDiskCrmDocumentUpload? Upload { get; private set; }
        public byte[]? UploadBytes { get; private set; }

        public Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = "/CRM" });

        public Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = request.RootPath ?? "/CRM" });

        public Task<YandexDiskConnectionTestDto> TestConnectionAsync(TestYandexDiskConnectionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskConnectionTestDto { Success = true, RootPath = "/CRM" });

        public Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskListResponseDto());

        public Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });

        public Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });

        public Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = toPath ?? "/" });

        public Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });

        public Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = path ?? "/" });

        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskDownload("file.txt", "text/plain", []));

        public Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskTextFileDto { Path = path ?? "/", Content = string.Empty });

        public Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskOperationResultDto { Success = true, Path = request.Path ?? "/" });

        public Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncSettingsDto());

        public Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(UpdateYandexDiskCrmSyncSettingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncSettingsDto());

        public Task<YandexDiskCrmSyncPreviewDto> PreviewCrmSyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskCrmSyncPreviewDto());

        public async Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(YandexDiskCrmDocumentUpload upload, CancellationToken cancellationToken = default)
        {
            Upload = upload;
            using var memory = new MemoryStream();
            await upload.Content.CopyToAsync(memory, cancellationToken);
            UploadBytes = memory.ToArray();
            return new YandexDiskOperationResultDto { Success = true, Path = $"/CRM/{upload.OriginalFileName}" };
        }
    }
}
