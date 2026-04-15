using System.Security.Claims;
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
        Assert.Contains("Адрес: Самара, ул. Тестовая, д. 1", content.Content);
        Assert.Contains("Паспорт 1234 567890", content.Content);
        Assert.Contains("ИНН: 123456789012", content.Content);
        Assert.Contains("СНИЛС: 123-456-789 00", content.Content);
        Assert.Contains("Тел.: +7 999 123-45-67", content.Content);
        Assert.Contains("Email: client@example.com", content.Content);
        Assert.Contains("Договор: Договор № DOG-42 от 15.03.2026 Договор на сопровождение", content.Content);
        Assert.Contains("Дело: Дело о взыскании задолженности", content.Content);
        Assert.Contains("Комментарий для клиента:</strong> Тестовый счет", content.Content);
        Assert.Contains("Срок оплаты:</strong> 12.04.2026", content.Content);
        Assert.Contains("Счет не для бухгалтерии", content.Content);
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
        Assert.Contains("Паспорт 1234 567890", content.Content);
        Assert.Contains("код подразделения 123-456", content.Content);
        Assert.Contains("ИНН: 123456789012", content.Content);
        Assert.Contains("СНИЛС: 123-456-789 00", content.Content);
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
            AttachmentFileName = attachmentFileName;
            AttachmentBytes = attachmentBytes;
            AttachmentContentType = attachmentContentType;
            return Task.CompletedTask;
        }
    }
}
