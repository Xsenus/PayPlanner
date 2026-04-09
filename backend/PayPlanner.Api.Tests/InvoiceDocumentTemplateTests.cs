using System.Security.Claims;
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
        Assert.Contains("{{invoice.number}}", dto.DefaultTemplateHtml);
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

        var result = await controller.GetDocument(paymentId, "html", null, CancellationToken.None);

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

        var result = await controller.GetDocument(paymentId, "pdf", null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(pdfBytes, file.FileContents);
        Assert.Equal("invoice-25.pdf", file.FileDownloadName);
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

    private static async Task<int> CreateInvoiceDocumentFixtureAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Address = "Самара, ул. Тестовая, д. 1",
            Email = "client@example.com",
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.ClientPassports.Add(new ClientPassport
        {
            ClientId = client.Id,
            Series = "1234",
            Number = "567890",
            IssuedBy = "ОВД Тестового района",
            IssuedAt = new DateTime(2020, 1, 12),
            IsPrimary = true,
        });

        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 2),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = "25",
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            DocumentKind = DocumentKind.Invoice,
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Amount = 11_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Description = "Тестовый счет",
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

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
