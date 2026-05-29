using System.ComponentModel.DataAnnotations;
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
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ActsControllerTests
{
    [Fact]
    public void UpsertActRequest_AllowsLongCommentForLegalNotes()
    {
        var request = new UpsertActRequest
        {
            Number = "ACT-LONG-COMMENT",
            Date = new DateTime(2026, 5, 29),
            Amount = 18000m,
            Status = ActStatus.Created,
            Comment = new string('a', 1500),
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid, string.Join(Environment.NewLine, results.Select(item => item.ErrorMessage)));
    }

    [Fact]
    public async Task Create_FullAct_WithLongComment_PersistsCommentAndInvoiceLineItems_InLocalSqliteFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"payplanner-act-{Guid.NewGuid():N}.db");
        var db = await CreateFileContextAsync(dbPath);
        try
        {
            var client = await CreateClientAsync(db);
            var invoice = await CreateInvoiceAsync(
                db,
                "INV-LONG-COMMENT",
                new DateTime(2026, 5, 29),
                client.Id,
                new[]
                {
                    new InvoiceLineItem { Name = "Service A", Quantity = 1m, Unit = "service", Price = 8000m, SortOrder = 0 },
                    new InvoiceLineItem { Name = "Service B", Quantity = 1m, Unit = "service", Price = 10000m, SortOrder = 1 },
                });
            var longComment = string.Join(Environment.NewLine, Enumerable.Repeat("legal note", 180));
            var controller = new ActsController(db, new NoopFormRequirementsService());

            var result = await controller.Create(new UpsertActRequest
            {
                Number = "ACT-LONG-COMMENT",
                Date = new DateTime(2026, 5, 29),
                CreationMode = ActCreationMode.Full,
                InvoiceId = invoice.Id,
                InvoiceNumber = invoice.Account,
                Status = ActStatus.Created,
                ClientId = client.Id,
                Comment = longComment,
            }, CancellationToken.None);

            var created = Assert.IsType<CreatedResult>(result.Result);
            var dto = Assert.IsType<ActDto>(created.Value);
            Assert.Equal(18000m, dto.Amount);
            Assert.Equal(longComment, dto.Comment);
            Assert.Equal(2, dto.LineItems.Count);

            var stored = await db.Acts.Include(item => item.LineItems).SingleAsync(item => item.Id == dto.Id);
            Assert.Equal(longComment, stored.Comment);
            Assert.Equal(2, stored.LineItems.Count);
        }
        finally
        {
            await db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Create_AssignsCurrentUserAsResponsible_WhenResponsibleNotProvided()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var responsible = await CreateUserAsync(
            db,
            "Тестовый Ответственный",
            "responsible@example.com",
            isActive: true,
            isApproved: true);

        var controller = new ActsController(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, responsible.Id.ToString()),
                    ], "TestAuth")),
                },
            },
        };

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-AUTH-1",
                Date = new DateTime(2026, 4, 20),
                Amount = 9_500m,
                CreationMode = ActCreationMode.Manual,
                ClientId = client.Id,
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(responsible.Id, dto.ResponsibleId);
        Assert.Equal(responsible.FullName, dto.ResponsibleName);
    }

    [Fact]
    public async Task SigningWorkflowActions_UpdateStatusAndTimestamps()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var createAction = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-SIGN-1",
                Date = new DateTime(2026, 4, 20),
                Amount = 9_500m,
                CreationMode = ActCreationMode.Manual,
                ClientId = client.Id,
                Status = ActStatus.Created,
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<ActDto>(created.Value);

        var sendAction = await controller.SendToSigning(createdDto.Id, CancellationToken.None);

        var sendOk = Assert.IsType<OkObjectResult>(sendAction.Result);
        var sentDto = Assert.IsType<ActDto>(sendOk.Value);
        Assert.Equal(ActStatus.Transferred, sentDto.Status);
        Assert.Equal(ActWorkflowStatus.OnSigning, sentDto.WorkflowStatus);
        Assert.NotNull(sentDto.SentForSigningAt);
        Assert.Null(sentDto.SignedAt);

        var signAction = await controller.MarkSigned(createdDto.Id, CancellationToken.None);

        var signOk = Assert.IsType<OkObjectResult>(signAction.Result);
        var signedDto = Assert.IsType<ActDto>(signOk.Value);
        Assert.Equal(ActStatus.Signed, signedDto.Status);
        Assert.Equal(ActWorkflowStatus.Signed, signedDto.WorkflowStatus);
        Assert.NotNull(signedDto.SentForSigningAt);
        Assert.NotNull(signedDto.SignedAt);

        db.ChangeTracker.Clear();
        var stored = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == createdDto.Id);
        Assert.Equal(ActStatus.Signed, stored.Status);
        Assert.Equal(ActWorkflowStatus.Signed, stored.WorkflowStatus);
        Assert.NotNull(stored.SentForSigningAt);
        Assert.NotNull(stored.SignedAt);
    }

    [Fact]
    public async Task UpdateWorkflowStatus_AcceptsRequiresClientSignature()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var createAction = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-SIGN-CLIENT-1",
                Date = new DateTime(2026, 4, 20),
                Amount = 9_500m,
                CreationMode = ActCreationMode.Manual,
                ClientId = client.Id,
                Status = ActStatus.Created,
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<ActDto>(created.Value);

        var updateAction = await controller.UpdateWorkflowStatus(
            createdDto.Id,
            new UpdateNamedStatusRequest { Status = ActWorkflowStatus.RequiresClientSignature },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var dto = Assert.IsType<ActDto>(ok.Value);
        Assert.Equal(ActStatus.Created, dto.Status);
        Assert.Equal(ActWorkflowStatus.RequiresClientSignature, dto.WorkflowStatus);
        Assert.Null(dto.SentForSigningAt);
        Assert.Null(dto.SignedAt);
    }

    [Fact]
    public async Task Create_DerivesLegacyStatusFromWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-STATUS-SYNC-1",
                Date = new DateTime(2026, 4, 20),
                Amount = 9_500m,
                CreationMode = ActCreationMode.Manual,
                ClientId = client.Id,
                Status = ActStatus.Signed,
                WorkflowStatus = ActWorkflowStatus.RequiresClientSignature,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(ActStatus.Created, dto.Status);
        Assert.Equal(ActWorkflowStatus.RequiresClientSignature, dto.WorkflowStatus);
    }

    [Fact]
    public async Task Cancel_StoresReasonAndUpdatesWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var actor = await CreateUserAsync(db, "Администратор", "admin@example.com", isActive: true, isApproved: true);
        var act = await CreateManualActAsync(db, client.Id, "ACT-CANCEL-1", 7_500m, ActStatus.Signed);
        act.WorkflowStatus = ActWorkflowStatus.Signed;
        act.Comment = "Исходный комментарий";
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()),
                        new Claim(ClaimTypes.Role, "admin"),
                    ], "TestAuth")),
                },
            },
        };

        var action = await controller.Cancel(
            act.Id,
            new CancelActRequest { Reason = "Клиент отказался подписывать акт" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ActDto>(ok.Value);
        Assert.Equal(ActStatus.Terminated, dto.Status);
        Assert.Equal(ActWorkflowStatus.Cancelled, dto.WorkflowStatus);
        Assert.NotNull(dto.CancelledAt);
        Assert.Equal(actor.Id, dto.CancelledByUserId);
        Assert.Equal("Клиент отказался подписывать акт", dto.CancelledReason);
        Assert.Contains("Клиент отказался подписывать акт", dto.Comment, StringComparison.Ordinal);

        db.ChangeTracker.Clear();
        var stored = await db.Acts.AsNoTracking().SingleAsync(item => item.Id == act.Id);
        Assert.Equal(ActStatus.Terminated, stored.Status);
        Assert.Equal(ActWorkflowStatus.Cancelled, stored.WorkflowStatus);
        Assert.NotNull(stored.CancelledAt);
        Assert.Equal(actor.Id, stored.CancelledByUserId);
        Assert.Equal("Клиент отказался подписывать акт", stored.CancelledReason);
        Assert.Contains("Акт аннулирован", stored.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_RejectsEmptyReason()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-CANCEL-EMPTY", 7_500m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Cancel(
            act.Id,
            new CancelActRequest { Reason = "   " },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("reason", message, StringComparison.OrdinalIgnoreCase);
    }

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
        Assert.Equal(invoicePayment.Id, dto.InvoiceId);
        Assert.Equal("INV-100", dto.InvoiceNumber);
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
    public async Task GetDocument_ForPartialAct_RendersOnlySelectedActLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-104-P",
            new DateTime(2026, 4, 15),
            client.Id,
            [
                new InvoiceLineItem { Name = "Prep docs", Quantity = 2m, Unit = "pcs", Price = 4_000m, SortOrder = 0 },
                new InvoiceLineItem { Name = "Court visit", Quantity = 1m, Unit = "pcs", Price = 8_000m, SortOrder = 1 },
            ]);

        var sourceKey = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == invoicePayment.Id && item.SortOrder == 0)
            .Select(item => $"db:{item.Id}")
            .SingleAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var createRequest = new UpsertActRequest
        {
            Number = "ACT-104-P",
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

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);

        var documentResult = await controller.GetDocument(dto.Id, "html", true, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(documentResult);
        var normalizedHtml = content.Content!.Replace("&nbsp;", " ").Replace("\u00A0", " ");
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("ACT-104-P", normalizedHtml);
        Assert.Contains("Prep docs", normalizedHtml);
        Assert.DoesNotContain("Court visit", normalizedHtml);
        Assert.Contains("Всего оказано услуг 1", normalizedHtml);
        Assert.Contains("4 000,00", normalizedHtml);
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
        Assert.Contains("doc-main-title", normalizedHtml);
        Assert.Contains("counterparty-table", normalizedHtml);
        Assert.Contains("Акт № 1199 от 25 декабря 2025 г.", normalizedHtml);
        Assert.Contains("Счет № 182 от 24.12.2025", normalizedHtml);
        Assert.Contains("к/с 30101810745374525104", normalizedHtml);
        Assert.Contains("Паспорт 36 07 № 763332", normalizedHtml);
        Assert.Contains("Всего оказано услуг 2", normalizedHtml);
        Assert.Contains("20 000,00", normalizedHtml);
        Assert.Contains("ИСПОЛНИТЕЛЬ", normalizedHtml);
    }

    [Fact]
    public void ActDocumentTemplateEngine_UsesInvoiceDateAndInvoiceClientPassport()
    {
        var actClient = new Client
        {
            Id = 100,
            Name = "Сорокина Светлана Геннадьевна",
            Address = "446103, Россия, Самарская обл, г Чапаевск, ул Достоевского, д 8",
        };

        var invoiceClient = new Client
        {
            Id = 101,
            Name = "Сорокина Светлана Геннадьевна",
            Passports =
            [
                new ClientPassport
                {
                    Series = "9999",
                    Number = "000001",
                    IssuedBy = "Тестовый паспорт",
                    UpdatedAt = new DateTime(2026, 4, 20),
                },
                new ClientPassport
                {
                    Series = "8113",
                    Number = "527051",
                    IssuedBy = "ТП УФМС России по республике Бурятия",
                    IssuedAt = new DateTime(2013, 10, 9),
                    DepartmentCode = "030-035",
                    IsPrimary = true,
                    UpdatedAt = new DateTime(2026, 4, 15),
                },
            ],
        };

        var act = new Act
        {
            Number = "34",
            Date = new DateTime(2026, 4, 24),
            Amount = 17_500m,
            Client = actClient,
            Invoice = new Payment
            {
                Type = PaymentType.Income,
                Account = "34",
                AccountDate = new DateTime(2026, 4, 15),
                Date = new DateTime(2026, 4, 27),
                Client = invoiceClient,
                Invoice = new Invoice
                {
                    Number = "34",
                    Date = new DateTime(2026, 4, 15),
                },
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "Юридические услуги",
                    Quantity = 1m,
                    Unit = "услуга",
                    Price = 17_500m,
                    SortOrder = 0,
                },
            ],
            mainCompany: null,
            includeSignatureAndSeal: false);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");

        Assert.Contains("Счет № 34 от 15.04.2026", normalizedHtml);
        Assert.DoesNotContain("Счет-договор № 34", normalizedHtml);
        Assert.DoesNotContain("Счет № 34 от 27.04.2026", normalizedHtml);
        Assert.Contains("Паспорт 8113 № 527051", normalizedHtml);
        Assert.DoesNotContain("9999", normalizedHtml);
    }

    [Fact]
    public void ActDocumentTemplateEngine_RendersExtendedBuyerBlock_LikeInvoice()
    {
        var invoiceClient = new Client
        {
            Id = 101,
            Name = "Тестовый клиент",
            Notes = """__PP_CLIENT_PROFILE_V2__:{"inn":"123456789012","snils":"123-456-789 00","passportSeries":"1234","passportNumber":"567890","passportIssuedBy":"ОВД Тестового района","passportIssueDate":"2020-01-12T00:00:00","passportDepartmentCode":"123-456","passportEntries":[{"series":"1234","number":"567890","issuedBy":"ОВД Тестового района","issueDate":"2020-01-12T00:00:00","departmentCode":"123-456"}]}""",
            Addresses =
            [
                new AddressRecord
                {
                    Type = AddressType.Registration,
                    Value = "Самара, ул. Тестовая, д. 1",
                    IsPrimary = true,
                },
            ],
        };

        var act = new Act
        {
            Number = "35",
            Date = new DateTime(2026, 4, 24),
            Amount = 17_500m,
            Invoice = new Payment
            {
                Type = PaymentType.Income,
                Account = "35",
                AccountDate = new DateTime(2026, 4, 15),
                Client = invoiceClient,
                Invoice = new Invoice
                {
                    Number = "35",
                    Date = new DateTime(2026, 4, 15),
                },
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "Юридические услуги",
                    Quantity = 1m,
                    Unit = "услуга",
                    Price = 17_500m,
                    SortOrder = 0,
                },
            ],
            mainCompany: null,
            includeSignatureAndSeal: false);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");

        Assert.Contains("Тестовый клиент, ИНН 123456789012, СНИЛС 123-456-789 00", normalizedHtml);
        Assert.Contains("Паспорт 1234 № 567890", normalizedHtml);
        Assert.Contains("Самара, ул. Тестовая, д. 1", normalizedHtml);

        var buyerNameIndex = normalizedHtml.IndexOf("Тестовый клиент", StringComparison.Ordinal);
        var innIndex = normalizedHtml.IndexOf("ИНН 123456789012", StringComparison.Ordinal);
        var snilsIndex = normalizedHtml.IndexOf("СНИЛС 123-456-789 00", StringComparison.Ordinal);
        var passportIndex = normalizedHtml.IndexOf("Паспорт 1234 № 567890", StringComparison.Ordinal);
        var addressIndex = normalizedHtml.IndexOf("Самара, ул. Тестовая, д. 1", StringComparison.Ordinal);
        Assert.True(buyerNameIndex >= 0, "Buyer name should be present in the rendered act.");
        Assert.True(innIndex > buyerNameIndex, "ИНН should be rendered after the buyer name.");
        Assert.True(snilsIndex > innIndex, "СНИЛС should be rendered after ИНН.");
        Assert.True(passportIndex > snilsIndex, "Passport details should be rendered after СНИЛС.");
        Assert.True(addressIndex > passportIndex, "Address should be rendered after passport details.");
    }

    [Fact]
    public void ActDocumentTemplateEngine_RendersLegalEntityBuyerBlock_LikeInvoice()
    {
        var legalEntity = new LegalEntity
        {
            Id = 301,
            ShortName = "ООО Ромашка",
            FullName = "Общество с ограниченной ответственностью \"Ромашка\"",
            Inn = "6311000000",
            Kpp = "631101001",
            Ogrn = "1236300000000",
            Director = "Романов Роман Романович",
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
        var act = new Act
        {
            Number = "36",
            Date = new DateTime(2026, 4, 24),
            Amount = 45_000m,
            Invoice = new Payment
            {
                Type = PaymentType.Income,
                Account = "77",
                AccountDate = new DateTime(2026, 4, 10),
                LegalEntity = legalEntity,
                CounterpartyName = "ООО Ромашка",
                Invoice = new Invoice
                {
                    Number = "77",
                    Date = new DateTime(2026, 4, 10),
                },
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "Юридические услуги",
                    Quantity = 1m,
                    Unit = "услуга",
                    Price = 45_000m,
                    SortOrder = 0,
                },
            ],
            mainCompany: null,
            includeSignatureAndSeal: false);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");

        Assert.Contains("Общество с ограниченной ответственностью &quot;Ромашка&quot;, ИНН 6311000000, КПП 631101001, ОГРН 1236300000000", normalizedHtml);
        Assert.Contains("Самара, ул. Юридическая, д. 5", normalizedHtml);
        Assert.Contains("Счет № 77 от 10.04.2026", normalizedHtml);
        Assert.Contains("/ Романов Р. Р. /", normalizedHtml);
    }

    [Fact]
    public void ActDocumentTemplateEngine_RendersKubStyleSlashSignaturesForBothParties()
    {
        var act = new Act
        {
            Number = "36",
            Date = new DateTime(2026, 4, 24),
            Amount = 10_000m,
            Client = new Client
            {
                Name = "Губанова Оксана Ивановна",
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "Юридические услуги",
                    Quantity = 1m,
                    Unit = "услуга",
                    Price = 10_000m,
                    SortOrder = 0,
                },
            ],
            new LegalEntity
            {
                ShortName = "ИП Кожеваткин Андрей Владимирович",
                FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
                Director = "Кожеваткин Андрей Владимирович",
                DirectorSignatureDataUrl = "data:image/png;base64,signature-demo",
                SealDataUrl = "data:image/png;base64,seal-demo",
            },
            includeSignatureAndSeal: true);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");

        Assert.Contains("signature-sign-cell", normalizedHtml);
        Assert.Equal(2, Regex.Matches(normalizedHtml, "<span class=\"signature-mark\">", RegexOptions.CultureInvariant).Count);
        Assert.Contains("data:image/png;base64,signature-demo", normalizedHtml);
        Assert.Contains("data:image/png;base64,seal-demo", normalizedHtml);
        Assert.Contains("/ Кожеваткин А. В. /", normalizedHtml);
        Assert.Contains("/ Губанова О. И. /", normalizedHtml);
        Assert.DoesNotContain("signature-lines-table", normalizedHtml);
        Assert.DoesNotContain("расшифровка подписи", normalizedHtml);
    }

    [Fact]
    public void ActDocumentTemplateEngine_RendersExpenseActWithSupplierAsExecutorAndCompanyAsCustomer()
    {
        var supplier = new Client
        {
            Id = 201,
            Name = "Исаева Марина",
        };
        var caseClient = new Client
        {
            Id = 202,
            Name = "Павлов Павел Олегович",
        };
        var company = new LegalEntity
        {
            ShortName = "ИП Кожеваткин Андрей Владимирович",
            FullName = "Индивидуальный предприниматель Кожеваткин Андрей Владимирович",
            Inn = "633009620345",
            Address = "446200, Самарская обл, г Новокуйбышевск, ул. Миронова д. 9",
            TreasuryAccountNumber = "40802810414500006073",
            TreasuryBankName = "ТОЧКА ПАО БАНКА \"ФК ОТКРЫТИЕ\"",
            TreasuryBankBik = "044525999",
            TreasuryCorrAccountNumber = "30101810845250000999",
            Director = "Кожеваткин Андрей Владимирович",
            DirectorSignatureDataUrl = "data:image/png;base64,signature-demo",
            SealDataUrl = "data:image/png;base64,seal-demo",
        };
        var act = new Act
        {
            Number = "160",
            Date = new DateTime(2022, 6, 17),
            Amount = 1_200m,
            Client = caseClient,
            InvoiceNumber = "160",
            Invoice = new Payment
            {
                Type = PaymentType.Expense,
                Account = "160",
                AccountDate = new DateTime(2022, 6, 16),
                Client = supplier,
                ExpenseClient = caseClient,
            },
        };

        var html = ActDocumentTemplateEngine.RenderHtml(
            act,
            [
                new ActLineItem
                {
                    Name = "Подготовка письма о возможности заключения мирового соглашения",
                    Quantity = 1m,
                    Unit = "усл.ед.",
                    Price = 1_200m,
                    SortOrder = 0,
                },
            ],
            company,
            includeSignatureAndSeal: true);

        var normalizedHtml = html.Replace("&nbsp;", " ").Replace("\u00A0", " ");
        var supplierIndex = normalizedHtml.IndexOf("Исаева Марина", StringComparison.Ordinal);
        var companyIndex = normalizedHtml.IndexOf("Индивидуальный предприниматель Кожеваткин Андрей Владимирович", StringComparison.Ordinal);

        Assert.True(supplierIndex >= 0, "Поставщик должен быть исполнителем расходного акта.");
        Assert.True(companyIndex > supplierIndex, "Наша организация должна быть заказчиком расходного акта.");
        Assert.Contains("Счет № 160 от 16.06.2022", normalizedHtml);
        Assert.Contains("р/с 40802810414500006073", normalizedHtml);
        Assert.Contains("ТОЧКА ПАО БАНКА &quot;ФК ОТКРЫТИЕ&quot;", normalizedHtml);
        Assert.DoesNotContain("Павлов Павел Олегович", normalizedHtml);
        Assert.DoesNotContain("data:image/png;base64,signature-demo", normalizedHtml);
        Assert.DoesNotContain("data:image/png;base64,seal-demo", normalizedHtml);
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

    [Fact]
    public async Task Create_ManualAct_RoundTripsStandaloneFields()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var request = new UpsertActRequest
        {
            Number = "ACT-200",
            Title = "Ручной акт",
            Date = new DateTime(2026, 4, 20),
            Amount = 12_345m,
            CreationMode = ActCreationMode.Manual,
            InvoiceNumber = "MAN-200",
            CounterpartyInn = "1234567890",
            Status = ActStatus.Transferred,
            ClientId = client.Id,
            Comment = "Услуги без привязки к счёту",
        };

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);

        Assert.Equal("ACT-200", dto.Number);
        Assert.Equal("Ручной акт", dto.Title);
        Assert.Equal(12_345m, dto.Amount);
        Assert.Equal(ActCreationMode.Manual, dto.CreationMode);
        Assert.Equal("MAN-200", dto.InvoiceNumber);
        Assert.Equal("1234567890", dto.CounterpartyInn);
        Assert.Equal(ActStatus.Transferred, dto.Status);
        Assert.Equal(ActWorkflowStatus.OnSigning, dto.WorkflowStatus);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal("Услуги без привязки к счёту", dto.Comment);
        Assert.Empty(dto.LineItems);

        var storedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == dto.Id);
        Assert.Equal(ActWorkflowStatus.OnSigning, storedAct.WorkflowStatus);
    }

    [Fact]
    public async Task Update_ManualModeWithoutSelection_ClearsExistingInvoiceLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-201",
            new DateTime(2026, 4, 18),
            client.Id,
            [
                new InvoiceLineItem { Name = "Подготовка позиции", Quantity = 1m, Unit = "усл.ед.", Price = 7_000m, SortOrder = 0 },
                new InvoiceLineItem { Name = "Выезд на заседание", Quantity = 2m, Unit = "усл.ед.", Price = 4_000m, SortOrder = 1 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var createRequest = new UpsertActRequest
        {
            Number = "ACT-201",
            Date = new DateTime(2026, 4, 19),
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Created,
            CreationMode = ActCreationMode.Full,
        };

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(15_000m, dto.Amount);
        Assert.Equal(2, dto.LineItems.Count);

        var updateRequest = new UpsertActRequest
        {
            Number = "ACT-201-UPDATED",
            Title = "Обновлённый акт",
            Date = new DateTime(2026, 4, 20),
            Amount = 1m,
            InvoiceId = invoicePayment.Id,
            ClientId = client.Id,
            Status = ActStatus.Signed,
            WorkflowStatus = ActWorkflowStatus.RequiresClientSignature,
            CreationMode = ActCreationMode.Manual,
            Comment = "Ручной режим должен убрать позиции счета",
        };

        var updateAction = await controller.Update(dto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updated = Assert.IsType<ActDto>(ok.Value);
        Assert.Equal("ACT-201-UPDATED", updated.Number);
        Assert.Equal("Обновлённый акт", updated.Title);
        Assert.Equal(ActStatus.Created, updated.Status);
        Assert.Equal(ActWorkflowStatus.RequiresClientSignature, updated.WorkflowStatus);
        Assert.Equal("Ручной режим должен убрать позиции счета", updated.Comment);
        Assert.Equal(ActCreationMode.Manual, updated.CreationMode);
        Assert.Equal(1m, updated.Amount);
        Assert.Empty(updated.LineItems);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<ActLineItem>().AsNoTracking().Where(item => item.ActId == dto.Id).ToListAsync());
    }

    [Fact]
    public async Task Delete_SoftDeletesAct_AndRemovesStoredLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-202",
            new DateTime(2026, 4, 18),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга для удаления", Quantity = 1m, Unit = "усл.ед.", Price = 5_500m, SortOrder = 0 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var createAction = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-202",
                Date = new DateTime(2026, 4, 19),
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<ActDto>(created.Value);

        var deleteAction = await controller.Delete(dto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteAction);
        Assert.Null(await db.Acts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == dto.Id));
        Assert.Equal(0, await db.Set<ActLineItem>().AsNoTracking().CountAsync(item => item.ActId == dto.Id));
    }

    [Fact]
    public async Task Get_FiltersByClientIds_ParsesInputAndPaginates()
    {
        await using var db = await CreateContextAsync();
        var firstClient = await CreateClientAsync(db, "Первый клиент");
        var secondClient = await CreateClientAsync(db, "Второй клиент");

        db.Acts.AddRange(
            new Act
            {
                Number = "ACT-301",
                Date = new DateTime(2026, 4, 21),
                Amount = 1_000m,
                ClientId = firstClient.Id,
                Status = ActStatus.Created,
            },
            new Act
            {
                Number = "ACT-302",
                Date = new DateTime(2026, 4, 22),
                Amount = 2_000m,
                ClientId = secondClient.Id,
                Status = ActStatus.Created,
            },
            new Act
            {
                Number = "ACT-303",
                Date = new DateTime(2026, 4, 23),
                Amount = 3_000m,
                ClientId = secondClient.Id,
                Status = ActStatus.Signed,
            });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Get(
            null,
            null,
            null,
            null,
            null,
            null,
            $"0, bad, {secondClient.Id}",
            null,
            null,
            "number",
            "asc",
            1,
            1,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var response = ok.Value;
        Assert.NotNull(response);
        var items = Assert.IsAssignableFrom<IEnumerable<ActDto>>(response.GetType().GetProperty("items")?.GetValue(response)).ToList();
        var total = Assert.IsType<int>(response.GetType().GetProperty("total")?.GetValue(response));
        var page = Assert.IsType<int>(response.GetType().GetProperty("page")?.GetValue(response));
        var pageSize = Assert.IsType<int>(response.GetType().GetProperty("pageSize")?.GetValue(response));

        var firstItem = Assert.Single(items);
        Assert.Equal(2, total);
        Assert.Equal(1, page);
        Assert.Equal(1, pageSize);
        Assert.Equal("ACT-302", firstItem.Number);
        Assert.Equal(secondClient.Id, firstItem.ClientId);
    }

    [Fact]
    public async Task Get_FiltersByContractId_UsingManualAndInvoiceLinks()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var contract = await CreateContractAsync(db, "ACT-CTR-1");

        var canonicalInvoice = await CreateInvoiceAsync(
            db,
            "INV-ACT-CAN",
            new DateTime(2026, 4, 25),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 5_000m, SortOrder = 0 },
            ]);
        canonicalInvoice.ContractId = contract.Id;

        var legacyInvoice = await CreateInvoiceAsync(
            db,
            "INV-ACT-LEG",
            new DateTime(2026, 4, 26),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 6_000m, SortOrder = 0 },
            ]);

        var manualAct = await CreateManualActAsync(db, client.Id, "ACT-CMAN", 2_000m, ActStatus.Created);
        await CreateManualActAsync(db, client.Id, "ACT-CAN", 5_000m, ActStatus.Created, canonicalInvoice.Id);
        await CreateManualActAsync(db, client.Id, "ACT-LEG", 6_000m, ActStatus.Created, legacyInvoice.Id);
        await CreateManualActAsync(db, client.Id, "ACT-OUT", 7_000m, ActStatus.Created);

        db.ContractActs.Add(new ContractAct
        {
            ContractId = contract.Id,
            ActId = manualAct.Id,
        });
        db.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = legacyInvoice.Id,
        });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Get(
            null,
            null,
            null,
            null,
            null,
            contract.Id,
            null,
            null,
            null,
            "number",
            "asc",
            1,
            20,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var response = ok.Value;
        Assert.NotNull(response);
        var items = Assert.IsAssignableFrom<IEnumerable<ActDto>>(response.GetType().GetProperty("items")?.GetValue(response)).ToList();

        Assert.Equal(new[] { "ACT-CAN", "ACT-CMAN", "ACT-LEG" }, items.Select(item => item.Number).ToArray());
        Assert.DoesNotContain(items, item => item.Number == "ACT-OUT");
    }

    [Fact]
    public async Task Get_FiltersByInvoiceIdAndPaymentId()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var invoice = await CreateInvoiceAsync(
            db,
            "INV-ACT-REL",
            new DateTime(2026, 4, 27),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 8_000m, SortOrder = 0 },
            ]);

        var linkedPayment = new Payment
        {
            Amount = 4_000m,
            CounterpartyName = "Тестовый клиент",
            Date = invoice.Date.AddDays(1),
            PlannedDate = invoice.Date.AddDays(1),
            Description = "Платеж по счету INV-ACT-REL",
            DocumentKind = DocumentKind.Payment,
            InvoiceId = invoice.InvoiceId,
            ClientId = client.Id,
            Account = invoice.Account,
            AccountDate = invoice.AccountDate,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };
        db.Payments.Add(linkedPayment);
        await db.SaveChangesAsync();

        var linkedAct = await CreateManualActAsync(db, client.Id, "ACT-INV-FILTER", 8_000m, ActStatus.Created, invoice.Id);
        var outsideAct = await CreateManualActAsync(db, client.Id, "ACT-OUT-FILTER", 9_000m, ActStatus.Created);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var invoiceAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            type: null,
            clientId: null,
            contractId: null,
            clientIds: null,
            responsibleId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            invoiceId: invoice.Id);

        var invoiceOk = Assert.IsType<OkObjectResult>(invoiceAction);
        var invoiceItems = Assert.IsAssignableFrom<IEnumerable<ActDto>>(invoiceOk.Value?.GetType().GetProperty("items")?.GetValue(invoiceOk.Value)).ToList();
        Assert.Collection(invoiceItems, item => Assert.Equal(linkedAct.Id, item.Id));

        var paymentAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            type: null,
            clientId: null,
            contractId: null,
            clientIds: null,
            responsibleId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            paymentId: linkedPayment.Id);

        var paymentOk = Assert.IsType<OkObjectResult>(paymentAction);
        var paymentItems = Assert.IsAssignableFrom<IEnumerable<ActDto>>(paymentOk.Value?.GetType().GetProperty("items")?.GetValue(paymentOk.Value)).ToList();
        Assert.Collection(paymentItems, item => Assert.Equal(linkedAct.Id, item.Id));
        Assert.DoesNotContain(paymentItems, item => item.Id == outsideAct.Id);
    }

    [Fact]
    public async Task Get_OmitsLineItemsByDefault_AndCanIncludeThem()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-LIST-LINES", 7_500m, ActStatus.Created);
        db.Set<ActLineItem>().Add(new ActLineItem
        {
            ActId = act.Id,
            Name = "Service",
            Quantity = 1m,
            Unit = "pcs",
            Price = 7_500m,
            SortOrder = 0,
        });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var listAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            type: null,
            clientId: null,
            contractId: null,
            clientIds: null,
            responsibleId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var listOk = Assert.IsType<OkObjectResult>(listAction);
        var listItems = Assert.IsAssignableFrom<IEnumerable<ActDto>>(listOk.Value?.GetType().GetProperty("items")?.GetValue(listOk.Value)).ToList();
        var listItem = Assert.Single(listItems);
        Assert.Empty(listItem.LineItems);

        var fullAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            type: null,
            clientId: null,
            contractId: null,
            clientIds: null,
            responsibleId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            includeLineItems: true);

        var fullOk = Assert.IsType<OkObjectResult>(fullAction);
        var fullItems = Assert.IsAssignableFrom<IEnumerable<ActDto>>(fullOk.Value?.GetType().GetProperty("items")?.GetValue(fullOk.Value)).ToList();
        Assert.Single(Assert.Single(fullItems).LineItems);
    }

    [Fact]
    public async Task GetById_ReturnsActDto_WhenActExists()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-304", 7_500m, ActStatus.Transferred);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetById(act.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ActDto>(ok.Value);
        Assert.Equal(act.Id, dto.Id);
        Assert.Equal("ACT-304", dto.Number);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(7_500m, dto.Amount);
        Assert.Equal(ActStatus.Transferred, dto.Status);
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenActMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetById(42_424, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task GetSummary_ReturnsTotalsForEveryStatus()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        db.Acts.AddRange(
            new Act
            {
                Number = "ACT-305-1",
                Date = new DateTime(2026, 4, 21),
                Amount = 10_000m,
                ClientId = client.Id,
                Status = ActStatus.Created,
            },
            new Act
            {
                Number = "ACT-305-2",
                Date = new DateTime(2026, 4, 21),
                Amount = 11_000m,
                ClientId = client.Id,
                Status = ActStatus.Transferred,
            },
            new Act
            {
                Number = "ACT-305-3",
                Date = new DateTime(2026, 4, 21),
                Amount = 12_000m,
                ClientId = client.Id,
                Status = ActStatus.Signed,
            },
            new Act
            {
                Number = "ACT-305-4",
                Date = new DateTime(2026, 4, 21),
                Amount = 13_000m,
                ClientId = client.Id,
                Status = ActStatus.Terminated,
            });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetSummary(null, null, null, null, null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var response = ok.Value;
        Assert.NotNull(response);

        var created = response.GetType().GetProperty("created")?.GetValue(response);
        var transferred = response.GetType().GetProperty("transferred")?.GetValue(response);
        var signed = response.GetType().GetProperty("signed")?.GetValue(response);
        var terminated = response.GetType().GetProperty("terminated")?.GetValue(response);
        Assert.NotNull(created);
        Assert.NotNull(transferred);
        Assert.NotNull(signed);
        Assert.NotNull(terminated);

        Assert.Equal(10_000m, Assert.IsType<decimal>(created.GetType().GetProperty("amount")?.GetValue(created)));
        Assert.Equal(1, Assert.IsType<int>(created.GetType().GetProperty("count")?.GetValue(created)));
        Assert.Equal(11_000m, Assert.IsType<decimal>(transferred.GetType().GetProperty("amount")?.GetValue(transferred)));
        Assert.Equal(1, Assert.IsType<int>(transferred.GetType().GetProperty("count")?.GetValue(transferred)));
        Assert.Equal(12_000m, Assert.IsType<decimal>(signed.GetType().GetProperty("amount")?.GetValue(signed)));
        Assert.Equal(1, Assert.IsType<int>(signed.GetType().GetProperty("count")?.GetValue(signed)));
        Assert.Equal(13_000m, Assert.IsType<decimal>(terminated.GetType().GetProperty("amount")?.GetValue(terminated)));
        Assert.Equal(1, Assert.IsType<int>(terminated.GetType().GetProperty("count")?.GetValue(terminated)));
        Assert.Equal(46_000m, Assert.IsType<decimal>(response.GetType().GetProperty("totalAmount")?.GetValue(response)));
        Assert.Equal(4, Assert.IsType<int>(response.GetType().GetProperty("totalCount")?.GetValue(response)));
    }

    [Fact]
    public async Task GetResponsibles_ReturnsOnlyApprovedActiveUsers_AndUsesEmailFallback()
    {
        await using var db = await CreateContextAsync();
        var namedUser = await CreateUserAsync(db, "Анна Иванова", "anna@example.com", isActive: true, isApproved: true);
        var fallbackUser = await CreateUserAsync(db, string.Empty, "fallback@example.com", isActive: true, isApproved: true);
        await CreateUserAsync(db, "Неактивный", "inactive@example.com", isActive: false, isApproved: true);
        await CreateUserAsync(db, "Не подтвержден", "pending@example.com", isActive: true, isApproved: false);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetResponsibles(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<ActResponsibleDto>>(ok.Value).ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == namedUser.Id && item.FullName == "Анна Иванова");
        Assert.Contains(items, item => item.Id == fallbackUser.Id && item.FullName == "fallback@example.com");
    }

    [Fact]
    public async Task Create_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());
        controller.ModelState.AddModelError("Number", "Required");

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-306",
                Date = new DateTime(2026, 4, 21),
                Amount = 1_000m,
                CreationMode = ActCreationMode.Manual,
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Number", problem.Errors.Keys);
    }

    [Fact]
    public async Task Create_ReturnsValidationProblem_WhenCreationModeIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-307",
                Date = new DateTime(2026, 4, 21),
                Amount = 1_000m,
                CreationMode = "broken-mode",
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains(nameof(UpsertActRequest.CreationMode), problem.Errors.Keys);
    }

    [Fact]
    public async Task Update_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-308", 2_500m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService());
        controller.ModelState.AddModelError("Number", "Required");

        var action = await controller.Update(
            act.Id,
            new UpsertActRequest
            {
                Number = "ACT-308-UPD",
                Date = new DateTime(2026, 4, 22),
                Amount = 2_700m,
                CreationMode = ActCreationMode.Manual,
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Number", problem.Errors.Keys);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenActMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Update(
            78_901,
            new UpsertActRequest
            {
                Number = "ACT-309",
                Date = new DateTime(2026, 4, 22),
                Amount = 1_500m,
                CreationMode = ActCreationMode.Manual,
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task Update_ReturnsValidationProblem_WhenCreationModeIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-310", 2_500m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Update(
            act.Id,
            new UpsertActRequest
            {
                Number = "ACT-310-UPD",
                Date = new DateTime(2026, 4, 22),
                Amount = 2_800m,
                CreationMode = "invalid-mode",
                Status = ActStatus.Created,
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains(nameof(UpsertActRequest.CreationMode), problem.Errors.Keys);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenActMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Delete(90_001, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task GetDocument_ReturnsNotFound_WhenActMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetDocument(70_001, "html", true, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task GetDocument_ReturnsWordFile_ForUnknownFormat_AndSanitizesFileName()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT:311/TEST?", 9_000m, ActStatus.Created);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = "not-json",
        });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetDocument(act.Id, "xlsx", false, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/msword", file.ContentType);
        Assert.EndsWith(".doc", file.FileDownloadName, StringComparison.Ordinal);
        Assert.DoesNotContain(":", file.FileDownloadName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", file.FileDownloadName, StringComparison.Ordinal);
        Assert.DoesNotContain("?", file.FileDownloadName, StringComparison.Ordinal);
        Assert.Contains("ACT:311/TEST?", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task GetDocument_ReturnsPdfFile_WhenPdfServiceConfigured()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-312", 9_500m, ActStatus.Created);

        var company = new LegalEntity
        {
            ShortName = "ООО Тест",
            FullName = "Общество с ограниченной ответственностью Тест",
            Inn = "1234567890",
            TreasuryBankBik = "044525104",
            TreasuryAccountNumber = "40802810414500006073",
            TreasuryCorrAccountNumber = "30101810745374525104",
        };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var pdfBytes = new byte[] { 1, 2, 3, 4 };
        var controller = new ActsController(db, new NoopFormRequirementsService(), new FakeInvoiceDocumentPdfService(pdfBytes));

        var action = await controller.GetDocument(act.Id, "pdf", false, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("act-ACT-312.pdf", file.FileDownloadName);
        Assert.Equal(pdfBytes, file.FileContents);
    }

    [Fact]
    public async Task GetDocument_ReturnsProblem_WhenPdfServiceMissing()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-313", 10_000m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetDocument(act.Id, "pdf", true, CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task SendActEmail_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-313-MAIL-VAL", 10_000m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService(), emailService: new RecordingEmailService());
        controller.ModelState.AddModelError("Email", "Invalid");

        var action = await controller.SendActEmail(
            act.Id,
            new SendInvoiceEmailRequest { Email = "broken" },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Email", details.Errors.Keys);
    }

    [Fact]
    public async Task SendActEmail_ReturnsNotFound_WhenActDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService(), emailService: new RecordingEmailService());

        var action = await controller.SendActEmail(
            999,
            new SendInvoiceEmailRequest { Email = "mail@example.com" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task SendActEmail_RequiresRecipient_WhenRequestAndClientEmailAreEmpty()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        client.Email = "   ";
        await db.SaveChangesAsync();
        var act = await CreateManualActAsync(db, client.Id, "ACT-313-MAIL-EMPTY", 10_000m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService(), emailService: new RecordingEmailService());

        var action = await controller.SendActEmail(
            act.Id,
            new SendInvoiceEmailRequest(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Recipient email is required.", badRequest.Value);
    }

    [Fact]
    public async Task SendActEmail_NormalizesHtmlFormatToWord_AndUsesCustomContent()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        client.Email = "client@example.com";
        await db.SaveChangesAsync();
        var act = await CreateManualActAsync(db, client.Id, "ACT-313-MAIL", 10_000m, ActStatus.Created);
        var emailService = new RecordingEmailService();
        var controller = new ActsController(db, new NoopFormRequirementsService(), emailService: emailService);

        var action = await controller.SendActEmail(
            act.Id,
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
        Assert.Equal("act-ACT-313-MAIL.doc", emailService.AttachmentFileName);
        Assert.Equal("application/msword", emailService.AttachmentContentType);
    }

    [Fact]
    public async Task SendActEmail_ReturnsProblem_WhenPdfServiceIsMissing()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        client.Email = "client@example.com";
        await db.SaveChangesAsync();
        var act = await CreateManualActAsync(db, client.Id, "ACT-313-MAIL-PDF", 10_000m, ActStatus.Created);
        var controller = new ActsController(db, new NoopFormRequirementsService(), emailService: new RecordingEmailService());

        var action = await controller.SendActEmail(
            act.Id,
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
    public async Task GetDocument_LoadsMainCompany_FromDefaultLegacyAndJsonSettings()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-314", 11_000m, ActStatus.Created);

        var company = new LegalEntity
        {
            ShortName = "ИП Тестов",
            FullName = "Индивидуальный предприниматель Тестов Тест",
            Inn = "123456789012",
        };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();
        var defaultContent = Assert.IsType<ContentResult>(await controller.GetDocument(act.Id, "html", false, CancellationToken.None));
        Assert.Contains(company.FullName, defaultContent.Content);

        db.SystemSettings.RemoveRange(db.SystemSettings);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();
        var legacyContent = Assert.IsType<ContentResult>(await controller.GetDocument(act.Id, "html", false, CancellationToken.None));
        Assert.Contains(company.FullName, legacyContent.Content);

        db.SystemSettings.RemoveRange(db.SystemSettings);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[{company.Id}]",
        });
        await db.SaveChangesAsync();
        var idsContent = Assert.IsType<ContentResult>(await controller.GetDocument(act.Id, "html", false, CancellationToken.None));
        Assert.Contains(company.FullName, idsContent.Content);

        db.SystemSettings.RemoveRange(db.SystemSettings);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = "not-json",
        });
        await db.SaveChangesAsync();
        var invalidJsonContent = Assert.IsType<ContentResult>(await controller.GetDocument(act.Id, "html", false, CancellationToken.None));
        Assert.DoesNotContain(company.FullName, invalidJsonContent.Content);
    }

    [Fact]
    public async Task GetDocument_PrefersPrimaryMainCompanyAddressRecord_OverLegacyAddress()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var act = await CreateManualActAsync(db, client.Id, "ACT-314-ADDR", 11_000m, ActStatus.Created);

        var company = new LegalEntity
        {
            ShortName = "ИП Тестов",
            FullName = "Индивидуальный предприниматель Тестов Тест",
            Inn = "123456789012",
            Address = "Старый адрес компании",
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
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
        });
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());
        var content = Assert.IsType<ContentResult>(await controller.GetDocument(act.Id, "html", false, CancellationToken.None));

        Assert.Contains("109012, Россия, г. Москва, ул. Ильинка, д. 9", content.Content);
        Assert.DoesNotContain("Старый адрес компании", content.Content);
    }

    [Fact]
    public async Task Create_RejectsSelectingLineItemsWithoutInvoice()
    {
        await using var db = await CreateContextAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-315",
                Date = new DateTime(2026, 4, 22),
                Amount = 1_000m,
                CreationMode = ActCreationMode.Manual,
                Status = ActStatus.Created,
                LineItems =
                [
                    new ActLineItemRequest
                    {
                        SourceKey = "db:1",
                        Quantity = 1m,
                    },
                ],
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("LineItems[0].Name", problem.Errors.Keys);
    }

    [Fact]
    public async Task Create_ManualAct_WithManualLineItems_CalculatesAmountAndPersistsRows()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-315-MANUAL",
                Date = new DateTime(2026, 4, 22),
                Amount = 1m,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Manual,
                LineItems =
                [
                    new ActLineItemRequest
                    {
                        SourceKey = "manual:1",
                        Name = "Подготовка документов",
                        Quantity = 2m,
                        Unit = "усл.",
                        Price = 3_500m,
                    },
                    new ActLineItemRequest
                    {
                        SourceKey = "manual:2",
                        Name = "Выезд в суд",
                        Quantity = 1m,
                        Unit = "усл.",
                        Price = 8_000m,
                    },
                ],
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(15_000m, dto.Amount);
        Assert.Equal(ActCreationMode.Manual, dto.CreationMode);
        Assert.Equal(2, dto.LineItems.Count);
        Assert.Collection(
            dto.LineItems.OrderBy(item => item.SourceSortOrder),
            first =>
            {
                Assert.Equal("manual:1", first.SourceKey);
                Assert.Equal("Подготовка документов", first.Name);
                Assert.Equal(2m, first.Quantity);
                Assert.Equal("усл.", first.Unit);
                Assert.Equal(3_500m, first.Price);
            },
            second =>
            {
                Assert.Equal("manual:2", second.SourceKey);
                Assert.Equal("Выезд в суд", second.Name);
                Assert.Equal(1m, second.Quantity);
                Assert.Equal("усл.", second.Unit);
                Assert.Equal(8_000m, second.Price);
            });

        var stored = await db.Set<ActLineItem>()
            .AsNoTracking()
            .Where(item => item.ActId == dto.Id)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Equal("manual:1", stored[0].SourceKey);
        Assert.Equal("Подготовка документов", stored[0].Name);
        Assert.Equal("manual:2", stored[1].SourceKey);
        Assert.Equal("Выезд в суд", stored[1].Name);
    }

    [Fact]
    public async Task Create_InvoiceWithoutStoredLineItems_FallsBackToManualAmount()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var invoice = new Invoice
        {
            Number = "INV-316",
            Date = new DateTime(2026, 4, 22),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoicePayment = new Payment
        {
            Amount = 0m,
            CounterpartyName = "Client",
            Date = invoice.Date,
            PlannedDate = invoice.Date,
            Description = "Счет без строк",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };
        db.Payments.Add(invoicePayment);
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-316",
                Date = new DateTime(2026, 4, 23),
                Amount = 4_321m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(4_321m, dto.Amount);
        Assert.Equal(ActCreationMode.Manual, dto.CreationMode);
        Assert.Empty(dto.LineItems);
    }

    [Fact]
    public async Task Create_InvoiceWithoutStoredLineItems_AllowsManualLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var invoice = new Invoice
        {
            Number = "INV-316-MANUAL",
            Date = new DateTime(2026, 4, 22),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoicePayment = new Payment
        {
            Amount = 0m,
            CounterpartyName = "Client",
            Date = invoice.Date,
            PlannedDate = invoice.Date,
            Description = "Счет без строк",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            ClientId = client.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };
        db.Payments.Add(invoicePayment);
        await db.SaveChangesAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-316-MANUAL",
                Date = new DateTime(2026, 4, 23),
                Amount = 0m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Manual,
                LineItems =
                [
                    new ActLineItemRequest
                    {
                        SourceKey = "manual:invoice-1",
                        Name = "Дополнительная консультация",
                        Quantity = 3m,
                        Unit = "час",
                        Price = 2_000m,
                    },
                ],
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(ActCreationMode.Manual, dto.CreationMode);
        Assert.Equal(invoicePayment.Id, dto.InvoiceId);
        Assert.Equal(6_000m, dto.Amount);
        var lineItem = Assert.Single(dto.LineItems);
        Assert.Equal("manual:invoice-1", lineItem.SourceKey);
        Assert.Equal("Дополнительная консультация", lineItem.Name);
        Assert.Equal(3m, lineItem.Quantity);
        Assert.Equal("час", lineItem.Unit);
        Assert.Equal(2_000m, lineItem.Price);
    }

    [Fact]
    public async Task Create_PartialModeWithInvoiceSelection_UsesSelectedLineItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-317",
            new DateTime(2026, 4, 22),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга по акту", Quantity = 2m, Unit = "усл.", Price = 1_500m, SortOrder = 0 },
            ]);

        var sourceKey = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == invoicePayment.Id)
            .Select(item => $"db:{item.Id}")
            .SingleAsync();

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-317",
                Date = new DateTime(2026, 4, 23),
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
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<ActDto>(created.Value);
        Assert.Equal(ActCreationMode.Partial, dto.CreationMode);
        Assert.Equal(1_500m, dto.Amount);
        Assert.Single(dto.LineItems);
    }

    [Fact]
    public async Task Create_PartialAct_RequiresAtLeastOneValidLineItem()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-318",
            new DateTime(2026, 4, 22),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 2_000m, SortOrder = 0 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-318",
                Date = new DateTime(2026, 4, 23),
                Amount = 0m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Partial,
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Create_PartialAct_RejectsUnknownInvoiceSourceKey()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-319",
            new DateTime(2026, 4, 22),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 2_000m, SortOrder = 0 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-319",
                Date = new DateTime(2026, 4, 23),
                Amount = 0m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Partial,
                LineItems =
                [
                    new ActLineItemRequest
                    {
                        SourceKey = "db:999999",
                        Quantity = 1m,
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Create_FullAct_ReturnsError_WhenInvoiceHasNoRemainingItems()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoiceAsync(
            db,
            "INV-320",
            new DateTime(2026, 4, 22),
            client.Id,
            [
                new InvoiceLineItem { Name = "Услуга", Quantity = 1m, Unit = "усл.", Price = 3_000m, SortOrder = 0 },
            ]);

        var controller = new ActsController(db, new NoopFormRequirementsService());

        var firstAction = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-320-1",
                Date = new DateTime(2026, 4, 23),
                Amount = 0m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);
        Assert.IsType<CreatedResult>(firstAction.Result);

        var secondAction = await controller.Create(
            new UpsertActRequest
            {
                Number = "ACT-320-2",
                Date = new DateTime(2026, 4, 24),
                Amount = 0m,
                InvoiceId = invoicePayment.Id,
                ClientId = client.Id,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Full,
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(secondAction.Result);
    }

    [Fact]
    public async Task GetDocument_UsesExportFallback_WhenNumberIsBlank()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        db.Acts.Add(new Act
        {
            Number = "   ",
            Date = new DateTime(2026, 4, 23),
            Amount = 5_000m,
            CreationMode = ActCreationMode.Manual,
            Status = ActStatus.Created,
            ClientId = client.Id,
        });
        await db.SaveChangesAsync();

        var actId = await db.Acts.AsNoTracking().Select(item => item.Id).SingleAsync();
        var controller = new ActsController(db, new NoopFormRequirementsService());

        var action = await controller.GetDocument(actId, "word", false, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("act-export.doc", file.FileDownloadName);
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

    private static async Task<PaymentContext> CreateFileContextAsync(string dbPath)
    {
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<Client> CreateClientAsync(PaymentContext db, string name = "Тестовый клиент")
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

    private static async Task<Contract> CreateContractAsync(PaymentContext db, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Date = new DateTime(2026, 4, 1),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private static async Task<Act> CreateManualActAsync(
        PaymentContext db,
        int? clientId,
        string number,
        decimal amount,
        ActStatus status,
        int? invoiceId = null)
    {
        var act = new Act
        {
            Number = number,
            Date = new DateTime(2026, 4, 21),
            Amount = amount,
            CreationMode = ActCreationMode.Manual,
            Status = status,
            ClientId = clientId,
            InvoiceId = invoiceId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Acts.Add(act);
        await db.SaveChangesAsync();
        return act;
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string fullName,
        string email,
        bool isActive,
        bool isApproved)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "manager");
        if (role is null)
        {
            role = new Role
            {
                Name = "manager",
                Description = "manager",
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            RoleId = role.Id,
            IsActive = isActive,
            IsApproved = isApproved,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
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

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

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
