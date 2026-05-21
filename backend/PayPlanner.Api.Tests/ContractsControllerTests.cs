using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ContractsControllerTests
{
    [Fact]
    public async Task Create_RequiresAmount_WhenBillingModeIsFixed()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-001",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = null,
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<ObjectResult>(actionResult.Result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains(nameof(request.Amount), details.Errors.Keys);
    }

    [Fact]
    public async Task Create_ClearsAmountAndValidUntil_WhenBillingModeIsActualAndContractIsOpenEnded()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-002",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            Amount = 15_000m,
            ValidUntil = new DateTime(2026, 12, 31),
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(created.Value);
        Assert.Equal(ContractBillingMode.Actual, dto.BillingMode);
        Assert.Equal(ContractStatus.Draft, dto.Status);
        Assert.True(dto.IsOpenEnded);
        Assert.Null(dto.Amount);
        Assert.Null(dto.ValidUntil);

        var contract = await db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractBillingMode.Actual, contract.BillingMode);
        Assert.Equal(ContractStatus.Draft, contract.Status);
        Assert.True(contract.IsOpenEnded);
        Assert.Null(contract.Amount);
        Assert.Null(contract.ValidUntil);
    }

    [Fact]
    public async Task Get_FiltersByContractStatus()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-WF-CONCLUDED",
                Date = new DateTime(2026, 4, 14),
                BillingMode = ContractBillingMode.Fixed,
                Status = ContractStatus.Concluded,
                Amount = 10_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);

        await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-WF-DRAFT",
                Date = new DateTime(2026, 4, 15),
                BillingMode = ContractBillingMode.Fixed,
                Status = ContractStatus.Draft,
                Amount = 8_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);

        var actionResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            search: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            status: ContractStatus.Concluded);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var items = GetAnonymousProperty<IEnumerable<ContractDto>>(ok.Value!, "items").ToList();
        var item = Assert.Single(items);
        Assert.Equal("CT-WF-CONCLUDED", item.Number);
        Assert.Equal(ContractStatus.Concluded, item.Status);
    }

    [Fact]
    public async Task Get_FiltersByRelatedInvoiceActAndPayment()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-REL-FILTER",
            Date = new DateTime(2026, 4, 21),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };
        var outsideContract = new Contract
        {
            Number = "CT-REL-OUT",
            Date = new DateTime(2026, 4, 22),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
        };
        db.Contracts.AddRange(contract, outsideContract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-CT-REL", new DateTime(2026, 4, 23), 14_000m);
        invoicePayment.ContractId = contract.Id;
        var act = await CreateActAsync(db, invoicePayment.Id, new DateTime(2026, 4, 24), 6_000m);
        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 25), 5_000m);
        payment.ContractId = contract.Id;
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var invoiceResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            invoiceId: invoicePayment.Id);
        var invoiceItems = GetAnonymousProperty<IEnumerable<ContractDto>>(Assert.IsType<OkObjectResult>(invoiceResult).Value!, "items").ToList();
        Assert.Collection(invoiceItems, item => Assert.Equal(contract.Id, item.Id));

        var actResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            actId: act.Id);
        var actItems = GetAnonymousProperty<IEnumerable<ContractDto>>(Assert.IsType<OkObjectResult>(actResult).Value!, "items").ToList();
        Assert.Collection(actItems, item => Assert.Equal(contract.Id, item.Id));

        var paymentResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            search: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            paymentId: payment.Id);
        var paymentItems = GetAnonymousProperty<IEnumerable<ContractDto>>(Assert.IsType<OkObjectResult>(paymentResult).Value!, "items").ToList();
        Assert.Collection(paymentItems, item => Assert.Equal(contract.Id, item.Id));
        Assert.DoesNotContain(paymentItems, item => item.Id == outsideContract.Id);
    }

    [Fact]
    public async Task SigningWorkflowActions_UpdateStatusAndTimestamps()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-SIGN-1",
                Date = new DateTime(2026, 4, 20),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 25_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var sendAction = await controller.SendToSigning(createdDto.Id, CancellationToken.None);

        var sendOk = Assert.IsType<OkObjectResult>(sendAction.Result);
        var sentDto = Assert.IsType<ContractDto>(sendOk.Value);
        Assert.Equal(ContractStatus.OnSigning, sentDto.Status);
        Assert.NotNull(sentDto.SentForSigningAt);
        Assert.Null(sentDto.ConcludedAt);

        var concludeAction = await controller.MarkConcluded(createdDto.Id, CancellationToken.None);

        var concludeOk = Assert.IsType<OkObjectResult>(concludeAction.Result);
        var concludedDto = Assert.IsType<ContractDto>(concludeOk.Value);
        Assert.Equal(ContractStatus.Concluded, concludedDto.Status);
        Assert.NotNull(concludedDto.SentForSigningAt);
        Assert.NotNull(concludedDto.ConcludedAt);

        db.ChangeTracker.Clear();
        var stored = await db.Contracts.AsNoTracking().SingleAsync(contract => contract.Id == createdDto.Id);
        Assert.Equal(ContractStatus.Concluded, stored.Status);
        Assert.NotNull(stored.SentForSigningAt);
        Assert.NotNull(stored.ConcludedAt);
    }

    [Fact]
    public async Task UpdateStatus_AcceptsRequiresClientSignature()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-SIGN-CLIENT-1",
                Date = new DateTime(2026, 4, 20),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 25_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var updateAction = await controller.UpdateStatus(
            createdDto.Id,
            new UpdateNamedStatusRequest { Status = ContractStatus.RequiresClientSignature },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(ContractStatus.RequiresClientSignature, dto.Status);
        Assert.Null(dto.SentForSigningAt);
        Assert.Null(dto.ConcludedAt);
    }

    [Fact]
    public async Task Terminate_StoresTermsAndUpdatesStatus()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-TERM-1",
                Date = new DateTime(2026, 4, 21),
                BillingMode = ContractBillingMode.Fixed,
                Status = ContractStatus.Concluded,
                Amount = 30_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var actionResult = await controller.Terminate(
            createdDto.Id,
            new TerminateContractRequest
            {
                TerminationDate = new DateTime(2026, 5, 2),
                Reason = "Соглашение сторон",
                Terms = "Услуги прекращаются с даты расторжения",
                FinancialTerms = "Без взаимных штрафов",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(ContractStatus.Terminated, dto.Status);
        Assert.Equal(new DateTime(2026, 5, 2), dto.TerminationDate);
        Assert.Equal("Соглашение сторон", dto.TerminationReason);
        Assert.Equal("Услуги прекращаются с даты расторжения", dto.TerminationTerms);
        Assert.Equal("Без взаимных штрафов", dto.TerminationFinancialTerms);

        db.ChangeTracker.Clear();
        var stored = await db.Contracts.AsNoTracking().SingleAsync(contract => contract.Id == createdDto.Id);
        Assert.Equal(ContractStatus.Terminated, stored.Status);
        Assert.Equal(new DateTime(2026, 5, 2), stored.TerminationDate);
        Assert.Equal("Услуги прекращаются с даты расторжения", stored.TerminationTerms);
    }

    [Fact]
    public async Task Cancel_AllowsDraftAndStoresReason()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-CANCEL-1",
                Date = new DateTime(2026, 4, 22),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 12_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var actionResult = await controller.Cancel(
            createdDto.Id,
            new CancelContractRequest { Reason = "Ошибочно созданный договор" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(ContractStatus.Cancelled, dto.Status);
        Assert.NotNull(dto.CancelledAt);
        Assert.Equal("Ошибочно созданный договор", dto.CancelledReason);

        db.ChangeTracker.Clear();
        var stored = await db.Contracts.AsNoTracking().SingleAsync(contract => contract.Id == createdDto.Id);
        Assert.Equal(ContractStatus.Cancelled, stored.Status);
        Assert.NotNull(stored.CancelledAt);
        Assert.Equal("Ошибочно созданный договор", stored.CancelledReason);
    }

    [Fact]
    public async Task Cancel_RejectsConcludedContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-CANCEL-2",
                Date = new DateTime(2026, 4, 23),
                BillingMode = ContractBillingMode.Fixed,
                Status = ContractStatus.Concluded,
                Amount = 12_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var actionResult = await controller.Cancel(
            createdDto.Id,
            new CancelContractRequest { Reason = "Нельзя аннулировать заключённый договор" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Contains("черновиков", Assert.IsType<string>(badRequest.Value));
    }

    [Fact]
    public async Task GetDocument_ReturnsContractHtml()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db, email: "client@example.com");
        var controller = new ContractsController(db, new NoopFormRequirementsService());

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-DOC-1",
                Date = new DateTime(2026, 5, 21),
                Title = "Юридическое сопровождение",
                Description = "Консультации и подготовка документов",
                BillingMode = ContractBillingMode.Fixed,
                Amount = 45_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var actionResult = await controller.GetDocument(createdDto.Id, "html", templateKey: null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Договор № CT-DOC-1", content.Content);
        Assert.Contains("Юридическое сопровождение", content.Content);
        Assert.Contains("client@example.com", content.Content);
    }

    [Fact]
    public async Task SendEmail_UsesClientEmailAndAttachesContractDocument()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db, email: "client@example.com");
        var emailService = new CapturingEmailService();
        var controller = new ContractsController(db, new NoopFormRequirementsService(), emailService: emailService);

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-MAIL-1",
                Date = new DateTime(2026, 5, 21),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 45_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var actionResult = await controller.SendEmail(
            createdDto.Id,
            new SendInvoiceEmailRequest { Format = "word" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        Assert.True(GetAnonymousProperty<bool>(ok.Value!, "sent"));
        Assert.Equal("client@example.com", emailService.ToEmail);
        Assert.Equal("Договор № CT-MAIL-1", emailService.Subject);
        Assert.Equal("contract-CT-MAIL-1.doc", emailService.AttachmentFileName);
        Assert.Equal("application/msword", emailService.AttachmentContentType);
        Assert.Contains("Договор № CT-MAIL-1", Encoding.UTF8.GetString(emailService.AttachmentBytes!));
    }

    [Fact]
    public async Task DocumentTemplateSettings_StoresCustomActiveTemplate()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateAdminController(db);

        var actionResult = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "custom-contract",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "custom-contract",
                        Name = "Краткий договор",
                        Html = "<html><body>Шаблон {{contract.number}} / {{customer.name}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var settings = Assert.IsType<InvoiceDocumentTemplateSettingsDto>(ok.Value);
        Assert.Equal("custom-contract", settings.ActiveTemplateKey);
        Assert.Contains(settings.Tokens, token => token.Key == "contract.number");

        var stored = await db.SystemSettings.AsNoTracking().SingleAsync(setting => setting.Key == "contracts.documentTemplates");
        Assert.Contains("custom-contract", stored.Value);
    }

    [Fact]
    public async Task GetDocument_UsesConfiguredActiveTemplate()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = CreateAdminController(db);

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-TPL-1",
                Date = new DateTime(2026, 5, 22),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 19_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "custom-contract",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "custom-contract",
                        Name = "Краткий договор",
                        Html = "<html><body>Кастомный договор {{contract.number}} для {{customer.name}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        var actionResult = await controller.GetDocument(createdDto.Id, "html", null, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(actionResult);
        Assert.Contains("Кастомный договор CT-TPL-1", content.Content);
        Assert.Contains("Тестовый клиент", content.Content);
    }

    [Fact]
    public async Task GetTerminationAgreement_ReturnsAgreementForTerminatedContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var controller = CreateAdminController(db);

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-TERM-1",
                Date = new DateTime(2026, 5, 1),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 42_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        await controller.Terminate(
            createdDto.Id,
            new TerminateContractRequest
            {
                TerminationDate = new DateTime(2026, 5, 22),
                Terms = "Стороны прекращают обязательства по соглашению.",
                Reason = "Соглашение сторон",
                FinancialTerms = "Возвратов и штрафов нет.",
            },
            CancellationToken.None);

        var actionResult = await controller.GetTerminationAgreement(createdDto.Id, "html", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Соглашение о расторжении договора № CT-TERM-1", content.Content);
        Assert.Contains("Стороны прекращают обязательства", content.Content);
        Assert.Contains("Возвратов и штрафов нет", content.Content);
    }

    [Fact]
    public async Task Terminate_RequiresFinancialTerms_WhenContractHasUnpaidInvoices()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-UNPAID-1", new DateTime(2026, 5, 10), 15_000m);
        var controller = CreateAdminController(db);

        var createResult = await controller.Create(
            new UpsertContractRequest
            {
                Number = "CT-UNPAID-1",
                Date = new DateTime(2026, 5, 1),
                BillingMode = ContractBillingMode.Fixed,
                Amount = 15_000m,
                Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
                InvoiceIds = [invoicePayment.Id],
            },
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<ContractDto>(created.Value);

        var checkResult = await controller.GetTerminationCheck(createdDto.Id, CancellationToken.None);
        var checkOk = Assert.IsType<OkObjectResult>(checkResult.Result);
        var check = Assert.IsType<ContractTerminationCheckDto>(checkOk.Value);
        Assert.True(check.HasUnpaidInvoices);
        Assert.Equal(1, check.UnpaidInvoiceCount);
        Assert.Equal(15_000m, check.UnpaidInvoiceAmount);

        var terminateResult = await controller.Terminate(
            createdDto.Id,
            new TerminateContractRequest
            {
                TerminationDate = new DateTime(2026, 5, 22),
                Terms = "Расторжение по соглашению сторон.",
                Reason = "Соглашение сторон",
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(terminateResult.Result);
        Assert.Contains("неоплаченные счета", Assert.IsType<string>(badRequest.Value));
    }

    [Fact]
    public async Task Update_ClearsAmountAndValidUntil_WhenSwitchingToActualOpenEndedContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-003",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 20_000m,
            ValidUntil = new DateTime(2026, 12, 31),
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                    Comment = "Исходный клиент",
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = contract.Number,
            Date = contract.Date,
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            Amount = 99_999m,
            ValidUntil = new DateTime(2027, 1, 1),
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Update(contract.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(ContractBillingMode.Actual, dto.BillingMode);
        Assert.True(dto.IsOpenEnded);
        Assert.Null(dto.Amount);
        Assert.Null(dto.ValidUntil);

        var updatedContract = await db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractBillingMode.Actual, updatedContract.BillingMode);
        Assert.True(updatedContract.IsOpenEnded);
        Assert.Null(updatedContract.Amount);
        Assert.Null(updatedContract.ValidUntil);
    }

    [Fact]
    public async Task Create_RejectsFixedContract_WhenLinkedInvoicesExceedContractAmount()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-30", new DateTime(2026, 4, 14), 18_000m);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-004",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 10_000m,
            InvoiceIds = [invoicePayment.Id],
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<ObjectResult>(actionResult.Result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains(nameof(request.Amount), details.Errors.Keys);
    }

    [Fact]
    public async Task Create_AssignsCanonicalContractId_ToSelectedInvoices()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-31", new DateTime(2026, 4, 14), 8_000m);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-005",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 10_000m,
            InvoiceIds = [invoicePayment.Id],
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(created.Value);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
        Assert.Equal(8_000m, dto.InvoiceAmountTotal);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == invoicePayment.Id);
        Assert.Equal(dto.Id, storedPayment.ContractId);
    }

    [Fact]
    public async Task Create_AssignsCanonicalContractId_ToSelectedPayments()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 15), 3_500m);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-005A",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            PaymentIds = [payment.Id],
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(created.Value);
        Assert.Equal([payment.Id], dto.PaymentIds.OrderBy(id => id).ToArray());
        Assert.Equal(3_500m, dto.PaymentAmountTotal);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(dto.Id, storedPayment.ContractId);
    }

    [Fact]
    public async Task GetById_UsesCanonicalInvoiceContractLinks_ForTotalsAndIds()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 20_000m,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32", new DateTime(2026, 4, 14), 12_500m);
        invoicePayment.ContractId = contract.Id;
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(12_500m, dto.InvoiceAmountTotal);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Get_UsesCanonicalContractLinks_ForTotalsAndIds()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006-LIST",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32-LIST", new DateTime(2026, 4, 14), 12_500m);
        invoicePayment.ContractId = contract.Id;

        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 15), 5_500m);
        payment.ContractId = contract.Id;

        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.Get(
            from: null,
            to: null,
            clientId: client.Id,
            search: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var items = GetAnonymousProperty<IEnumerable<ContractDto>>(ok.Value!, "items").ToList();
        var dto = Assert.Single(items);
        Assert.Equal(contract.Id, dto.Id);
        Assert.Equal(12_500m, dto.InvoiceAmountTotal);
        Assert.Equal(5_500m, dto.PaymentAmountTotal);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
        Assert.Equal([payment.Id], dto.PaymentIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task GetById_KeepsLegacyManualInvoiceLinks_VisibleBeforeResave()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006A",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 20_000m,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32A", new DateTime(2026, 4, 14), 7_500m);
        db.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = invoicePayment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(7_500m, dto.InvoiceAmountTotal);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task GetById_UsesCanonicalPaymentContractLinks_ForTotalsAndIds_WithoutCountingInvoiceDocuments()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006B",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 16), 4_250m);
        payment.ContractId = contract.Id;

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32B", new DateTime(2026, 4, 16), 9_500m);
        invoicePayment.ContractId = contract.Id;
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(4_250m, dto.PaymentAmountTotal);
        Assert.Equal([payment.Id], dto.PaymentIds.OrderBy(id => id).ToArray());
        Assert.Equal(9_500m, dto.InvoiceAmountTotal);
    }

    [Fact]
    public async Task GetById_UsesActsLinkedThroughCanonicalInvoices_ForActAmountTotal_WithoutCreatingManualActIds()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006D",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32C", new DateTime(2026, 4, 17), 10_000m);
        invoicePayment.ContractId = contract.Id;

        var act = await CreateActAsync(db, invoicePayment.Id, new DateTime(2026, 4, 18), 3_800m);
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(3_800m, dto.ActAmountTotal);
        Assert.Empty(dto.ActIds);
        Assert.Equal(10_000m, dto.InvoiceAmountTotal);
    }

    [Fact]
    public async Task GetById_UsesActsLinkedThroughLegacyInvoices_ForActAmountTotal_WithoutCreatingManualActIds()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006E",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32D", new DateTime(2026, 4, 19), 11_000m);
        db.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = invoicePayment.Id,
        });

        var act = await CreateActAsync(db, invoicePayment.Id, new DateTime(2026, 4, 20), 4_600m);
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(4_600m, dto.ActAmountTotal);
        Assert.Empty(dto.ActIds);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task GetById_UsesPaymentsLinkedThroughCanonicalInvoices_ForPaymentAmountTotal_WithoutCreatingManualPaymentIds()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006F",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-32E", new DateTime(2026, 4, 21), 13_000m);
        invoicePayment.ContractId = contract.Id;

        var linkedPayment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 22), 5_700m);
        linkedPayment.InvoiceId = invoicePayment.InvoiceId;
        linkedPayment.Account = invoicePayment.Account;
        linkedPayment.AccountDate = invoicePayment.AccountDate;

        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(13_000m, dto.InvoiceAmountTotal);
        Assert.Equal(5_700m, dto.PaymentAmountTotal);
        Assert.Equal([invoicePayment.Id], dto.InvoiceIds.OrderBy(id => id).ToArray());
        Assert.Equal([linkedPayment.Id], dto.PaymentIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task GetById_KeepsLegacyManualPaymentLinks_VisibleBeforeResave()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-006C",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 17), 6_100m);
        db.ContractPayments.Add(new ContractPayment
        {
            ContractId = contract.Id,
            PaymentId = payment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.GetById(contract.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(6_100m, dto.PaymentAmountTotal);
        Assert.Equal([payment.Id], dto.PaymentIds.OrderBy(id => id).ToArray());
    }

    [Theory]
    [InlineData("иванов")]
    [InlineData("ИВАНОВ")]
    [InlineData("иВаНоВ")]
    public async Task Get_SearchesByLinkedClientName_IgnoringCase(string searchTerm)
    {
        await using var db = await CreateContextAsync();
        var matchingClient = new Client
        {
            Name = "Иванов Иван Иванович",
            Company = "ООО Ромашка",
        };
        var otherClient = new Client
        {
            Name = "Петров Петр Петрович",
            Company = "ООО Василек",
        };
        db.Clients.AddRange(matchingClient, otherClient);
        await db.SaveChangesAsync();

        db.Contracts.AddRange(
            new Contract
            {
                Number = "CT-CLIENT-MATCH",
                Date = new DateTime(2026, 4, 14),
                BillingMode = ContractBillingMode.Actual,
                IsOpenEnded = true,
                ClientContracts =
                {
                    new ClientContract
                    {
                        ClientId = matchingClient.Id,
                    },
                },
            },
            new Contract
            {
                Number = "CT-CLIENT-OTHER",
                Date = new DateTime(2026, 4, 15),
                BillingMode = ContractBillingMode.Actual,
                IsOpenEnded = true,
                ClientContracts =
                {
                    new ClientContract
                    {
                        ClientId = otherClient.Id,
                    },
                },
            });
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var actionResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            search: searchTerm,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var items = GetAnonymousProperty<IEnumerable<ContractDto>>(ok.Value!, "items").ToList();
        var total = GetAnonymousProperty<int>(ok.Value!, "total");

        Assert.Equal(1, total);
        var item = Assert.Single(items);
        Assert.Equal("CT-CLIENT-MATCH", item.Number);
        Assert.Equal("Иванов Иван Иванович", Assert.Single(item.Clients).Name);
    }

    [Fact]
    public async Task Update_ClearsCanonicalContractId_WhenInvoiceIsRemovedFromContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-007",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 20_000m,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-33", new DateTime(2026, 4, 14), 5_000m);
        invoicePayment.ContractId = contract.Id;
        db.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = invoicePayment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = contract.Number,
            Date = contract.Date,
            BillingMode = ContractBillingMode.Fixed,
            Amount = contract.Amount,
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            InvoiceIds = [],
        };

        var actionResult = await controller.Update(contract.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Empty(dto.InvoiceIds);
        Assert.Equal(0m, dto.InvoiceAmountTotal);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == invoicePayment.Id);
        Assert.Null(storedPayment.ContractId);
        Assert.False(await db.ContractInvoices.AsNoTracking().AnyAsync(item => item.ContractId == contract.Id && item.InvoiceId == invoicePayment.Id));
    }

    [Fact]
    public async Task Update_ClearsCanonicalContractId_WhenPaymentIsRemovedFromContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-007A",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var payment = await CreateStandalonePaymentAsync(db, new DateTime(2026, 4, 18), 5_200m);
        payment.ContractId = contract.Id;
        db.ContractPayments.Add(new ContractPayment
        {
            ContractId = contract.Id,
            PaymentId = payment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = contract.Number,
            Date = contract.Date,
            BillingMode = contract.BillingMode,
            IsOpenEnded = contract.IsOpenEnded,
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
            PaymentIds = [],
        };

        var actionResult = await controller.Update(contract.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Empty(dto.PaymentIds);
        Assert.Equal(0m, dto.PaymentAmountTotal);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Null(storedPayment.ContractId);
        Assert.False(await db.ContractPayments.AsNoTracking().AnyAsync(item => item.ContractId == contract.Id && item.PaymentId == payment.Id));
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

    private static async Task<Client> CreateClientAsync(PaymentContext db, string? email = null)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
            Email = email ?? string.Empty,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static ContractsController CreateAdminController(PaymentContext db)
    {
        var controller = new ContractsController(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "1"),
                        new Claim(ClaimTypes.Name, "Администратор"),
                        new Claim(ClaimTypes.Role, "admin"),
                    ], "Test")),
                },
            },
        };

        return controller;
    }

    private static async Task<Payment> CreateInvoicePaymentAsync(PaymentContext db, string invoiceNumber, DateTime date, decimal amount)
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
            Amount = amount,
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = $"Счет {invoiceNumber}",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<Payment> CreateStandalonePaymentAsync(PaymentContext db, DateTime date, decimal amount)
    {
        var payment = new Payment
        {
            Amount = amount,
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = $"Платеж {date:dd.MM.yyyy}",
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<Act> CreateActAsync(PaymentContext db, int? invoiceId, DateTime date, decimal amount)
    {
        var act = new Act
        {
            Number = $"ACT-{date:yyyyMMddHHmmss}",
            Date = date,
            Amount = amount,
            CreationMode = ActCreationMode.Manual,
            InvoiceId = invoiceId,
            Status = ActStatus.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Acts.Add(act);
        await db.SaveChangesAsync();
        return act;
    }

    private static T GetAnonymousProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property.GetValue(value));
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class CapturingEmailService : IEmailService
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
