using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Acts;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ActsControllerTests
{
    [Fact]
    public async Task Create_FullAct_UsesAllRemainingInvoiceLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-100",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Подготовка иска", Quantity = 1m, Unit = "усл.ед.", Price = 10_000m, SortOrder = 0 },
                new InvoiceLineItem { Name = "Участие в заседании", Quantity = 2m, Unit = "усл.ед.", Price = 5_000m, SortOrder = 1 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var request = new UpsertActRequest
        {
            Number = "ACT-100",
            Date = new DateTime(2026, 4, 16),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Full,
        };

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(20_000m, dto.Amount);
        Assert.Equal(ActCreationMode.Full, dto.CreationMode);
        Assert.Equal(2, dto.LineItems.Count);
        Assert.Collection(
            dto.LineItems.OrderBy(item => item.SourceSortOrder),
            first =>
            {
                Assert.Equal("Подготовка иска", first.Name);
                Assert.Equal(1m, first.Quantity);
            },
            second =>
            {
                Assert.Equal("Участие в заседании", second.Name);
                Assert.Equal(2m, second.Quantity);
            });
    }

    [Fact]
    public async Task Create_PartialAct_RejectsQuantityAboveRemaining()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-101",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Подготовка жалобы", Quantity = 1m, Unit = "усл.ед.", Price = 7_000m, SortOrder = 0 },
            ]);

        var sourceKey = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == invoicePayment.Id)
            .Select(item => $"db:{item.Id}")
            .SingleAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var request = new UpsertActRequest
        {
            Number = "ACT-101",
            Date = new DateTime(2026, 4, 16),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Partial,
            LineItems =
            [
                new ActLineItemRequest
                {
                    SourceKey = sourceKey,
                    Quantity = 2m,
                },
            ],
        };

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("остатка", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_SecondAct_UsesOnlyRemainingInvoiceLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-102",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Подготовка документов", Quantity = 2m, Unit = "усл.ед.", Price = 4_000m, SortOrder = 0 },
                new InvoiceLineItem { Name = "Выезд в суд", Quantity = 1m, Unit = "усл.ед.", Price = 8_000m, SortOrder = 1 },
            ]);

        var sourceKey = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == invoicePayment.Id && item.SortOrder == 0)
            .Select(item => $"db:{item.Id}")
            .SingleAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var partialRequest = new UpsertActRequest
        {
            Number = "ACT-102-1",
            Date = new DateTime(2026, 4, 16),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Partial,
            LineItems =
            [
                new ActLineItemRequest
                {
                    SourceKey = sourceKey,
                    Quantity = 1m,
                },
            ],
        };

        var partialAction = await controller.Create(partialRequest, CancellationToken.None);
        Assert.IsType<CreatedResult>(partialAction.Result);

        var fullRequest = new UpsertActRequest
        {
            Number = "ACT-102-2",
            Date = new DateTime(2026, 4, 17),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Full,
        };

        var fullAction = await controller.Create(fullRequest, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(fullAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(12_000m, dto.Amount);
        Assert.Collection(
            dto.LineItems.OrderBy(item => item.SourceSortOrder),
            first =>
            {
                Assert.Equal("Подготовка документов", first.Name);
                Assert.Equal(1m, first.Quantity);
            },
            second =>
            {
                Assert.Equal("Выезд в суд", second.Name);
                Assert.Equal(1m, second.Quantity);
            });
    }

    [Fact]
    public async Task Create_TerminatedAct_DoesNotConsumeInvoiceLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-103",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Консультация", Quantity = 1m, Unit = "усл.ед.", Price = 3_000m, SortOrder = 0 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var terminatedRequest = new UpsertActRequest
        {
            Number = "ACT-103-1",
            Date = new DateTime(2026, 4, 16),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Terminated,
            CreationMode = ActCreationMode.Full,
        };

        var terminatedAction = await controller.Create(terminatedRequest, CancellationToken.None);
        Assert.IsType<CreatedResult>(terminatedAction.Result);

        var nextRequest = new UpsertActRequest
        {
            Number = "ACT-103-2",
            Date = new DateTime(2026, 4, 17),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Full,
        };

        var nextAction = await controller.Create(nextRequest, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(nextAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        var lineItem = Assert.Single(dto.LineItems);
        Assert.Equal("Консультация", lineItem.Name);
        Assert.Equal(1m, lineItem.Quantity);
        Assert.Equal(3_000m, dto.Amount);
    }

    [Fact]
    public async Task GetDocument_RendersActLineItems_AsHtml()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-104",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Подготовка заявления", Quantity = 1m, Unit = "усл.ед.", Price = 9_000m, SortOrder = 0 },
                new InvoiceLineItem { Name = "Выезд в суд", Quantity = 1m, Unit = "усл.ед.", Price = 6_000m, SortOrder = 1 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var createRequest = new UpsertActRequest
        {
            Number = "ACT-104",
            Date = new DateTime(2026, 4, 16),
            Amount = 0m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Full,
        };

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);

        var documentResult = await controller.GetDocument(dto.Id, "html", true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(documentResult);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("ACT-104", content.Content);
        Assert.Contains("Подготовка заявления", content.Content);
        Assert.Contains("Выезд в суд", content.Content);
    }

    [Fact]
    public void ActDocumentTemplateEngine_RendersReferenceLikeTemplate()
    {
        var client = new Client
        {
            Name = "Хрустеев Сергей Петрович",
            Address = "Самарская область, г. Новокуйбышевск, ул. Суворова, д. 21А, кв. 11",
            Passports =
            [
                new ClientPassport
                {
                    Series = "36 07",
                    Number = "763332",
                },
            ],
        };

        var act = new Act
        {
            Number = "1199",
            Date = new DateTime(2025, 12, 25),
            Amount = 20_000m,
            Client = client,
            InvoiceNumber = "182",
            Invoice = new Payment
            {
                Account = "182",
                Date = new DateTime(2025, 12, 24),
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "АДМ/ Ознакомление с материалами дела (за 1 том)",
                    Quantity = 1m,
                    Unit = "усл.ед.",
                    Price = 10_000m,
                    SortOrder = 0,
                },
                new ActLineItem
                {
                    Name = "ПР/ Юридические услуги / анализ дела",
                    Quantity = 1m,
                    Unit = "услуга",
                    Price = 10_000m,
                    SortOrder = 1,
                },
            ],
            new LegalEntity
            {
                ShortName = "ИП Кожеваткин Андрей Владимирович",
                FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                Inn = "633009620345",
                Address = "446200, Самарская обл, г Новокуйбышевск, ул. Миронова д. 9",
                TreasuryAccountNumber = "40802810414500006073",
                TreasuryBankName = "ООО \"Банк Точка\"",
                TreasuryBankBik = "044525104",
                TreasuryCorrAccountNumber = "30101810745374525104",
            },
            includeSignatureAndSeal: false);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");

        Assert.Contains("document-template act-template", normalizedHtml);
        Assert.Contains("Акт № 1199 от 25 декабря 2025 г.", normalizedHtml);
        Assert.Contains("Счет-договор № 182 от 24.12.2025", normalizedHtml);
        Assert.Contains("к/с 30101810745374525104", normalizedHtml);
        Assert.Contains("Паспорт серия 36 07 № 763332", normalizedHtml);
        Assert.Contains("Всего оказано услуг 2", normalizedHtml);
        Assert.Contains("20 000,00", normalizedHtml);
        Assert.Contains("ИСПОЛНИТЕЛЬ", normalizedHtml);
    }

    [Fact]
    public async Task Get_FiltersActsByInvoiceType()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var incomeInvoicePayment = await CreateInvoiceAsync(
            db,
            "INV-105-IN",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Доходная услуга", Quantity = 1m, Unit = "усл.ед.", Price = 11_000m, SortOrder = 0 },
            ],
            PaymentType.Income);
        var expenseInvoicePayment = await CreateInvoiceAsync(
            db,
            "INV-105-EX",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Расходная услуга", Quantity = 1m, Unit = "усл.ед.", Price = 9_000m, SortOrder = 0 },
            ],
            PaymentType.Expense);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var incomeCreate = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-105-IN",
                Date = new DateTime(2026, 4, 16),
                Amount = 0m,
                InvoiceId = incomeInvoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);
        Assert.IsType<CreatedResult>(incomeCreate.Result);

        var expenseCreate = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-105-EX",
                Date = new DateTime(2026, 4, 16),
                Amount = 0m,
                InvoiceId = expenseInvoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);
        Assert.IsType<CreatedResult>(expenseCreate.Result);

        var action = await controller.Get(
            null,
            null,
            null,
            PaymentType.Income,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            20,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = Assert.IsAssignableFrom<IEnumerable<ActDto>>(ok.Value?.GetType().GetProperty("items")?.GetValue(ok.Value));
        var total = Assert.IsType<int>(ok.Value?.GetType().GetProperty("total")?.GetValue(ok.Value));
        var materialized = items.ToList();

        var onlyAct = Assert.Single(materialized);
        Assert.Equal(1, total);
        Assert.Equal("ACT-105-IN", onlyAct.Number);
        Assert.Equal(PaymentType.Income, onlyAct.Type);
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

    private static async Task<Client> CreateClientAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<Payment> CreateInvoiceAsync(
        PaymentContext db,
        string invoiceNumber,
        DateTime date,
        int clientId,
        IEnumerable<InvoiceLineItem> lineItems,
        PaymentType type = PaymentType.Income)
    {
        var invoice = new Invoice
        {
            Number = invoiceNumber,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = lineItems.Sum(item => item.Quantity * item.Price),
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = $"Счет {invoiceNumber}",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            ClientId = clientId,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = type,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var persistedLineItems = lineItems
            .Select(item => new InvoiceLineItem
            {
                PaymentId = payment.Id,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
                SortOrder = item.SortOrder,
            })
            .ToList();

        db.InvoiceLineItems.AddRange(persistedLineItems);
        await db.SaveChangesAsync();
        return payment;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }
}
