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
        Assert.Contains("<th class=\"sum\">Сумма</th>", dto.DefaultTemplateHtml);
        Assert.Contains("<td class=\"sum\">{{total}}</td>", dto.DefaultTemplateHtml);
        Assert.Contains(".counterparty-line", dto.DefaultTemplateHtml);
        Assert.Contains("overflow-wrap: anywhere;", dto.DefaultTemplateHtml);
        Assert.DoesNotContain("class=\"brand-column\" rowspan=\"2\"", dto.DefaultTemplateHtml);
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
        Assert.Contains("Тестовый клиент, ИНН 123456789012, СНИЛС 123-456-789 00", content.Content);
        Assert.Contains("Паспорт 1234 № 567890", content.Content);
        Assert.Contains("ИНН 123456789012", content.Content);
        Assert.Contains("СНИЛС 123-456-789 00", content.Content);
        Assert.Contains("Самара, ул. Тестовая, д. 1", content.Content);
        Assert.Contains(">Основание:</td>", content.Content);
        Assert.Contains("Договор № DOG-42 от 15.03.2026 Договор на сопровождение", content.Content);
        Assert.Contains("Комментарий для клиента:</strong> Тестовый счет", content.Content);
        Assert.Contains("Срок оплаты:</strong> 12.04.2026", content.Content);
        Assert.Contains("Счет не для бухгалтерии", content.Content);
        Assert.Contains("class=\"logo-wrap\" style=\"width: 40%; vertical-align: middle;\"", content.Content);
        Assert.Contains("<td style=\"width: 150px; vertical-align: top;\">", content.Content);
        Assert.DoesNotContain("class=\"brand-column\" rowspan=\"2\"", content.Content);
        Assert.DoesNotContain("Дело:", content.Content);
        Assert.DoesNotContain("Тел.:", content.Content);
        Assert.DoesNotContain("Email:", content.Content);

        var buyerNameIndex = content.Content.IndexOf("Тестовый клиент", StringComparison.Ordinal);
        var innIndex = content.Content.IndexOf("ИНН 123456789012", StringComparison.Ordinal);
        var snilsIndex = content.Content.IndexOf("СНИЛС 123-456-789 00", StringComparison.Ordinal);
        var passportIndex = content.Content.IndexOf("Паспорт 1234 № 567890", StringComparison.Ordinal);
        var addressIndex = content.Content.IndexOf("Самара, ул. Тестовая, д. 1", StringComparison.Ordinal);
        Assert.True(buyerNameIndex >= 0, "Buyer name should be present in the rendered document.");
        Assert.True(innIndex > buyerNameIndex, "ИНН should be rendered after the buyer name.");
        Assert.True(snilsIndex > innIndex, "СНИЛС should be rendered after ИНН.");
        Assert.True(passportIndex > snilsIndex, "Passport details should be rendered after СНИЛС.");
        Assert.True(addressIndex > passportIndex, "Address should be rendered after passport details.");
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
        Assert.Contains("<span class=\"counterparty-line\">Тестовый клиент, ИНН 123456789012, СНИЛС 123-456-789 00</span>", content.Content);
        Assert.Contains("<span class=\"counterparty-line\">Паспорт 1234 № 567890", content.Content);
        Assert.Contains("<span class=\"counterparty-line\">Самара, ул. Тестовая, д. 1</span>", content.Content);
        Assert.DoesNotContain("<br/>Паспорт", content.Content);
        Assert.DoesNotContain("<br/>Самара", content.Content);
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
        Assert.Contains("class=\"logo-wrap\" style=\"width: 40%; vertical-align: middle;\"", content.Content);
        Assert.Contains("<td style=\"width: 150px; vertical-align: top;\">", content.Content);
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
        Assert.Contains("Паспорт 1234 № 567890", content.Content);
        Assert.Contains("код подразделения 123-456", content.Content);
        Assert.Contains("ИНН 123456789012", content.Content);
        Assert.Contains("СНИЛС 123-456-789 00", content.Content);
        Assert.DoesNotContain("ИНН: 123456789012", content.Content);
        Assert.DoesNotContain("СНИЛС: 123-456-789 00", content.Content);
        Assert.Contains(">Основание:</td>", content.Content);
        Assert.Contains("Договор № DOG-42 от 15.03.2026 Договор на сопровождение", content.Content);
        Assert.DoesNotContain("Дело:", content.Content);

        var buyerNameIndex = content.Content.IndexOf("Тестовый клиент", StringComparison.Ordinal);
        var innIndex = content.Content.IndexOf("ИНН 123456789012", StringComparison.Ordinal);
        var snilsIndex = content.Content.IndexOf("СНИЛС 123-456-789 00", StringComparison.Ordinal);
        var passportIndex = content.Content.IndexOf("Паспорт 1234 № 567890", StringComparison.Ordinal);
        Assert.True(buyerNameIndex >= 0, "Buyer name should be present in the rendered document.");
        Assert.True(innIndex > buyerNameIndex, "ИНН should be rendered after the buyer name.");
        Assert.True(snilsIndex > innIndex, "СНИЛС should be rendered after ИНН.");
        Assert.True(passportIndex > snilsIndex, "Passport details should be rendered after СНИЛС.");
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
        Assert.Equal("application/msword", file.ContentType);
        Assert.Equal("invoice-25.doc", file.FileDownloadName);
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
        Assert.Equal("invoice-25.doc", emailService.AttachmentFileName);
        Assert.Equal("application/msword", emailService.AttachmentContentType);
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
        IInvoiceDocumentPdfService? pdfService = null)
    {
        var claims = new List<Claim>();
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        var controller = new InvoicesController(
            db,
            emailService ?? new NoopEmailService(),
            new NoopFormRequirementsService(),
            pdfService);
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
}
