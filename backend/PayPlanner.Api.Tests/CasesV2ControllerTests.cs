using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CasesV2ControllerTests
{
    [Fact]
    public async Task Get_AppliesOwnerVisibility()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(
            new ClientCase { Client = visibleClient, Title = "Видимое дело", Status = ClientCaseStatus.Open },
            new ClientCase { Client = hiddenClient, Title = "Скрытое дело", Status = ClientCaseStatus.Open });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = GetAnonymousItems(ok.Value);
        var item = Assert.Single(items);

        Assert.Equal("Видимое дело", item.Title);
    }

    [Fact]
    public async Task Get_AppliesRegistryScope()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var client = new Client
        {
            Name = "Клиент",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.AddRange(
            new ClientCase { Client = client, Title = "Мое активное дело", ResponsibleUserId = viewer.Id, Status = ClientCaseStatus.Open },
            new ClientCase { Client = client, Title = "Чужое активное дело", ResponsibleUserId = otherResponsible.Id, Status = ClientCaseStatus.OnHold },
            new ClientCase { Client = client, Title = "Закрытое дело", ResponsibleUserId = otherResponsible.Id, Status = ClientCaseStatus.Closed },
            new ClientCase { Client = client, Title = "Проигранное дело", ResponsibleUserId = viewer.Id, Status = ClientCaseStatus.Lost });
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        async Task<IReadOnlyList<ClientCase>> LoadScope(string scope)
        {
            var result = await controller.Get(
                clientId: null,
                legalEntityId: null,
                status: null,
                search: null,
                responsibleUserId: null,
                caseType: null,
                priority: null,
                from: null,
                to: null,
                minFee: null,
                maxFee: null,
                sortBy: "title",
                sortDir: "asc",
                page: 1,
                pageSize: 50,
                scope: scope,
                ct: CancellationToken.None);

            return GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value);
        }

        Assert.Equal(new[] { "Мое активное дело", "Чужое активное дело" }, (await LoadScope("active")).Select(item => item.Title).ToArray());
        Assert.Equal(new[] { "Закрытое дело", "Проигранное дело" }, (await LoadScope("archive")).Select(item => item.Title).ToArray());
        Assert.Equal(new[] { "Мое активное дело", "Проигранное дело" }, (await LoadScope("mine")).Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task Get_ClampsRegistryPageSize()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = viewer.Id };
        db.Clients.Add(client);
        for (var i = 0; i < 250; i++)
        {
            db.ClientCases.Add(new ClientCase
            {
                Client = client,
                Title = $"Дело {i:D3}",
                Status = ClientCaseStatus.Open,
            });
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: "title",
            sortDir: "asc",
            page: 1,
            pageSize: 500,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, GetAnonymousInt(ok.Value, "pageSize"));
        Assert.Equal(200, GetAnonymousItems(ok.Value).Count);
    }

    [Fact]
    public async Task Get_AddsFinanceSummaryForVisibleCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - финансы",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.AddRange(
            new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 50000m,
                PaidAmount = 35000m,
                Date = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            },
            new Payment
            {
                ExpenseClient = client,
                ClientCase = clientCase,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Amount = 7000m,
                PaidAmount = 2000m,
                Date = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            },
            new Payment
            {
                Client = client,
                ClientCase = clientCase,
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 999999m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc),
                PlannedDate = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc),
            });
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Просроченная задача",
                Status = "inProgress",
                DueAt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Будущая задача",
                Status = "waiting",
                DueAt = new DateTime(2100, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Земцов",
                Title = "Готовая задача",
                Status = "done",
                DueAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: null,
            search: null,
            responsibleUserId: null,
            caseType: null,
            priority: null,
            from: null,
            to: null,
            minFee: null,
            maxFee: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var item = Assert.Single(GetAnonymousItems(Assert.IsType<OkObjectResult>(result).Value));

        Assert.Equal(50000m, item.IncomeTotal);
        Assert.Equal(35000m, item.IncomePaid);
        Assert.Equal(15000m, item.IncomeOutstanding);
        Assert.Equal(1, item.IncomeCount);
        Assert.Equal(7000m, item.ExpenseTotal);
        Assert.Equal(2000m, item.ExpensePaid);
        Assert.Equal(5000m, item.ExpenseOutstanding);
        Assert.Equal(1, item.ExpenseCount);
        Assert.Equal(2, item.OpenTaskCount);
        Assert.Equal(1, item.CompletedTaskCount);
        Assert.Equal(1, item.OverdueTaskCount);
        Assert.Equal(33, item.TaskProgressPercent);
        Assert.NotNull(item.LastTaskAt);
        Assert.True(item.LastTaskAt >= new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GetSummary_ReturnsCompactCaseSnapshot()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner");
        var client = new Client
        {
            Name = "Client One",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Client One - bankruptcy",
            CaseNumber = "CASE-001",
            Status = ClientCaseStatus.Open,
            Priority = "high",
            Stage = "Preparation",
            FeeAmount = 50000m,
            StartDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            NextHearingAt = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
            LastViewedAt = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc),
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 30000m,
            PaidAmount = 20000m,
            Date = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
        });
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Overdue task",
                Status = "inProgress",
                DueAt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Future task",
                Status = "waiting",
                DueAt = new DateTime(2100, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            },
            new TaskJournalItem
            {
                Client = client,
                ClientCase = clientCase,
                ObjectName = "Client One",
                Title = "Completed task",
                Status = "done",
                DueAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetSummary(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseSummaryDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.Id);
        Assert.Equal("Client One - bankruptcy", dto.Title);
        Assert.Equal("CASE-001", dto.CaseNumber);
        Assert.Equal(ClientCaseStatus.Open.ToString(), dto.Status);
        Assert.Equal("high", dto.Priority);
        Assert.Equal("Preparation", dto.Stage);
        Assert.Equal("Client One", dto.OwnerName);
        Assert.Equal(owner.Id, dto.ResponsibleUserId);
        Assert.Equal("Owner", dto.ResponsibleName);
        Assert.Equal(2, dto.OpenTaskCount);
        Assert.Equal(1, dto.CompletedTaskCount);
        Assert.Equal(1, dto.OverdueTaskCount);
        Assert.Equal(33, dto.TaskProgressPercent);
        Assert.Equal(50000m, dto.FeeAmount);
        Assert.Equal(20000m, dto.PaidAmount);
        Assert.Equal(30000m, dto.DebtAmount);
        Assert.Equal(new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc), dto.NextHearingAt);
        Assert.NotNull(dto.LastTaskAt);
        Assert.True(dto.LastTaskAt >= new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc), dto.LastViewedAt);
    }

    [Fact]
    public async Task GetFinance_ReturnsDeduplicatedInvoicesContractsAndPayments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Owner", DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            ResponsibleUserId = owner.Id,
            Title = "Земцов - банкротство",
            CaseNumber = "CASE-008",
            Status = ClientCaseStatus.Open,
            FeeAmount = 25000m,
        };
        var contract = new Contract
        {
            Number = "1-2026",
            Title = "Банкротство",
            Date = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Amount = 25000m,
            Status = ContractStatus.Concluded,
        };
        var source = new PaymentSource
        {
            Name = "Касса",
            Description = "Наличные",
            ColorHex = "#10B981",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Contracts.Add(contract);
        db.PaymentSources.Add(source);
        await db.SaveChangesAsync();

        var invoiceRepresentative = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 25000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Счет №8",
        };
        var paymentOne = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            PaymentSource = source,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 15000m,
            PaidAmount = 15000m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Оплата по счету №8",
        };
        var paymentTwo = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            Contract = contract,
            PaymentSource = source,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 10000m,
            PaidAmount = 10000m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            Description = "Оплата по счету №8",
        };
        var cancelledPayment = new Payment
        {
            Client = client,
            ClientCase = clientCase,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Cancelled,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 100m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Payments.AddRange(invoiceRepresentative, paymentOne, paymentTwo, cancelledPayment);
        await db.SaveChangesAsync();

        db.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = invoiceRepresentative.Id,
        });
        db.ContractPayments.Add(new ContractPayment
        {
            ContractId = contract.Id,
            PaymentId = paymentOne.Id,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetFinance(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseFinanceDetailDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.CaseId);
        Assert.Equal("Земцов Виктор Александрович", dto.OwnerName);
        Assert.Equal(25000m, dto.Totals.IncomeTotal);
        Assert.Equal(25000m, dto.Totals.IncomePaid);
        Assert.Equal(0m, dto.Totals.OutstandingBalance);
        Assert.Equal(2, dto.Totals.IncomeCount);
        var invoice = Assert.Single(dto.Invoices);
        Assert.Equal("8", invoice.Number);
        Assert.Equal(25000m, invoice.Amount);
        Assert.Equal(PaymentStatus.Completed.ToString(), invoice.Status);
        var linkedContract = Assert.Single(dto.Contracts);
        Assert.Equal(contract.Id, linkedContract.Id);
        Assert.Equal("1-2026", linkedContract.Number);
        Assert.Equal(2, dto.Payments.Count);
        Assert.All(dto.Payments, payment => Assert.Equal("Касса", payment.PaymentSourceName));
    }

    [Fact]
    public async Task GetById_AndDelete_HideCasesFromOtherResponsibles()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(hiddenClient);
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        Assert.IsType<NotFoundResult>(await controller.GetById(hiddenCase.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Delete(hiddenCase.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsHiddenOwner()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Create(new ClientCase
        {
            ClientId = hiddenClient.Id,
            Title = "Попытка создать чужое дело",
            Status = ClientCaseStatus.Open,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.ClientCases.ToListAsync());
    }

    [Fact]
    public async Task Create_StoresRegistryDeadlineAndCourtFields()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var startDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var hearingAt = new DateTime(2026, 5, 20, 12, 30, 0, DateTimeKind.Utc);

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - банкротство",
            Description = "Судебное дело",
            Status = ClientCaseStatus.Open,
            CaseNumber = " 2026-001 ",
            CaseType = " Банкротство ",
            Stage = " Подготовка заявления ",
            ResponsibleUserId = owner.Id,
            StartDate = startDate,
            DueDate = dueDate,
            FeeAmount = 150000.50m,
            Priority = " high ",
            CourtName = " Арбитражный суд Самарской области ",
            CourtCaseNumber = " А55-12345/2026 ",
            CourtUrl = " https://kad.arbitr.ru/card ",
            NextHearingAt = hearingAt,
            Claimant = " Земцов Виктор Александрович ",
            Defendant = " Банк Точка ",
            ThirdParties = " ФНС\nСРО ",
            Representatives = " Кожеваткин А.В. ",
            JudgeName = " Иванова И.И. ",
            Courtroom = " зал 12 ",
            CourtSecretaryContacts = " secretary@example.test ",
            ClaimFiledAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
            AppealDeadlineAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            MotionDeadlineAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ObjectionDeadlineAt = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            DocumentsSummary = " Паспорт, договор ",
            EventHistory = " Заявление принято ",
            Strategy = " Подать ходатайство ",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var createdCase = Assert.IsType<ClientCase>(created.Value);

        Assert.Equal("2026-001", createdCase.CaseNumber);
        Assert.Equal("Банкротство", createdCase.CaseType);
        Assert.Equal("Подготовка заявления", createdCase.Stage);
        Assert.Equal(owner.Id, createdCase.ResponsibleUserId);
        Assert.Equal(startDate, createdCase.StartDate);
        Assert.Equal(dueDate, createdCase.DueDate);
        Assert.Equal(150000.50m, createdCase.FeeAmount);
        Assert.Equal("high", createdCase.Priority);
        Assert.Equal("Арбитражный суд Самарской области", createdCase.CourtName);
        Assert.Equal("А55-12345/2026", createdCase.CourtCaseNumber);
        Assert.Equal("https://kad.arbitr.ru/card", createdCase.CourtUrl);
        Assert.Equal(hearingAt, createdCase.NextHearingAt);
        Assert.Equal("Земцов Виктор Александрович", createdCase.Claimant);
        Assert.Equal("Банк Точка", createdCase.Defendant);
        Assert.Equal("ФНС\nСРО", createdCase.ThirdParties);
        Assert.Equal("Кожеваткин А.В.", createdCase.Representatives);
        Assert.Equal("Иванова И.И.", createdCase.JudgeName);
        Assert.Equal("зал 12", createdCase.Courtroom);
        Assert.Equal("secretary@example.test", createdCase.CourtSecretaryContacts);
        Assert.Equal(new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), createdCase.ClaimFiledAt);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), createdCase.AppealDeadlineAt);
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), createdCase.MotionDeadlineAt);
        Assert.Equal(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), createdCase.ObjectionDeadlineAt);
        Assert.Equal("Паспорт, договор", createdCase.DocumentsSummary);
        Assert.Equal("Заявление принято", createdCase.EventHistory);
        Assert.Equal("Подать ходатайство", createdCase.Strategy);
    }

    [Fact]
    public async Task Get_FiltersByRegistryFieldsAndDirectResponsible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.Add(hiddenClient);
        db.ClientCases.AddRange(
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Видимое дело по прямому ответственному",
                Status = ClientCaseStatus.Open,
                CaseNumber = "ZEM-001",
                CaseType = "банкротство",
                Stage = "Подготовка",
                ResponsibleUserId = owner.Id,
                FeeAmount = 120000m,
                Priority = "high",
                CourtName = "арбитражный суд",
                CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Не подходит по типу",
                Status = ClientCaseStatus.Open,
                CaseNumber = "FAM-001",
                CaseType = "Семейное право",
                ResponsibleUserId = owner.Id,
                FeeAmount = 90000m,
                Priority = "normal",
                CreatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            },
            new ClientCase
            {
                Client = hiddenClient,
                Title = "Скрытое дело",
                Status = ClientCaseStatus.Open,
                CaseNumber = "ZEM-002",
                CaseType = "банкротство",
                ResponsibleUserId = otherResponsible.Id,
                FeeAmount = 120000m,
                Priority = "high",
                CreatedAt = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Get(
            clientId: null,
            legalEntityId: null,
            status: ClientCaseStatus.Open,
            search: "арбитраж",
            responsibleUserId: owner.Id,
            caseType: "банк",
            priority: "HIGH",
            from: null,
            to: null,
            minFee: 100000m,
            maxFee: 130000m,
            sortBy: "feeAmount",
            sortDir: "desc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(GetAnonymousItems(ok.Value));

        Assert.Equal("ZEM-001", item.CaseNumber);
        Assert.Equal(owner.Id, item.ResponsibleUserId);
    }

    [Fact]
    public async Task CreateAndUpdate_SyncsProcessDeadlinesToTaskCalendarWithoutDuplicates()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var dueDate = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var courtDecision = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc);
        var calculatedAppealDeadline = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);
        var calculatedCassationDeadline = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
        var createResult = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - процессуальные сроки",
            Status = ClientCaseStatus.Open,
            DueDate = dueDate,
            CourtDecisionAt = courtDecision,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);
        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(createResult).Value);

        Assert.Equal(calculatedAppealDeadline, created.AppealDeadlineAt);
        Assert.Equal(calculatedCassationDeadline, created.CassationDeadlineAt);

        var tasks = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Where(item => item.ClientCaseId == created.Id)
            .OrderBy(item => item.Title)
            .ToListAsync();

        Assert.Equal(3, tasks.Count);
        var dueTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var appealTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:appeal", StringComparison.Ordinal));
        var cassationTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:cassation", StringComparison.Ordinal));
        Assert.Equal("control", dueTask.Type);
        Assert.Equal(dueDate.Date.AddHours(9), dueTask.DueAt);
        Assert.Contains("before7d", dueTask.RemindersJson, StringComparison.Ordinal);
        Assert.Contains("before3d", dueTask.RemindersJson, StringComparison.Ordinal);
        Assert.Equal(owner.Id, dueTask.AssigneeUserId);
        Assert.Contains(dueTask.Participants, item => item.Role == "assignee" && item.UserId == owner.Id);
        Assert.Equal(calculatedAppealDeadline.Date.AddHours(9), appealTask.DueAt);
        Assert.Equal(calculatedCassationDeadline.Date.AddHours(9), cassationTask.DueAt);

        var newDueDate = dueDate.AddDays(1);
        var updateResult = await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - процессуальные сроки",
            Status = ClientCaseStatus.Open,
            DueDate = newDueDate,
            CourtDecisionAt = null,
            AppealDeadlineAt = null,
            CassationDeadlineAt = null,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult);
        var afterUpdate = await db.TaskJournalItems
            .Where(item => item.ClientCaseId == created.Id)
            .ToListAsync();

        Assert.Equal(3, afterUpdate.Count);
        var updatedDueTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var cancelledAppealTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:appeal", StringComparison.Ordinal));
        var cancelledCassationTask = Assert.Single(afterUpdate, item => item.TagsJson.Contains("case-deadline:cassation", StringComparison.Ordinal));
        Assert.Equal(newDueDate.Date.AddHours(9), updatedDueTask.DueAt);
        Assert.NotEqual("cancelled", updatedDueTask.Status);
        Assert.Equal("cancelled", cancelledAppealTask.Status);
        Assert.Equal("cancelled", cancelledCassationTask.Status);
    }

    [Fact]
    public async Task Create_CalculatesProceduralDeadlinesWithRussianHolidays()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - срок на праздник",
            Status = ClientCaseStatus.Open,
            CourtDecisionAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), created.AppealDeadlineAt);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), created.CassationDeadlineAt);
    }

    [Fact]
    public async Task Update_ToClosed_ClosesLinkedActiveTasksAndDoesNotRecreateDeadlineTasks()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var dueDate = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var createResult = await controller.Create(new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - закрываемое дело",
            Status = ClientCaseStatus.Open,
            DueDate = dueDate,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);
        var created = Assert.IsType<ClientCase>(Assert.IsType<CreatedResult>(createResult).Value);

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientId = client.Id,
                ClientCaseId = created.Id,
                ObjectName = "Земцов Виктор Александрович - закрываемое дело",
                Title = "Ручная задача по делу",
                AssigneeUserId = owner.Id,
                AssigneeName = owner.FullName,
                AuthorUserId = owner.Id,
                AuthorName = owner.FullName,
                DueAt = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc),
                Status = "inProgress",
            },
            new TaskJournalItem
            {
                ClientId = client.Id,
                ClientCaseId = created.Id,
                ObjectName = "Земцов Виктор Александрович - закрываемое дело",
                Title = "Уже отмененная задача",
                AssigneeUserId = owner.Id,
                AssigneeName = owner.FullName,
                AuthorUserId = owner.Id,
                AuthorName = owner.FullName,
                DueAt = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc),
                Status = "cancelled",
            });
        await db.SaveChangesAsync();

        var result = await controller.Update(created.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - закрываемое дело",
            Status = ClientCaseStatus.Closed,
            DueDate = dueDate,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var tasks = await db.TaskJournalItems
            .Include(item => item.Comments)
            .Where(item => item.ClientCaseId == created.Id)
            .OrderBy(item => item.Title)
            .ToListAsync();

        var deadlineTask = Assert.Single(tasks, item => item.TagsJson.Contains("case-deadline:due", StringComparison.Ordinal));
        var manualTask = Assert.Single(tasks, item => item.Title == "Ручная задача по делу");
        var cancelledTask = Assert.Single(tasks, item => item.Title == "Уже отмененная задача");

        Assert.Equal("done", deadlineTask.Status);
        Assert.Equal("done", manualTask.Status);
        Assert.Equal("cancelled", cancelledTask.Status);
        Assert.Contains(manualTask.Comments, item => item.Text.Contains("Связанное дело закрыто", StringComparison.Ordinal));
        Assert.DoesNotContain(tasks, item => item.Status is not "done" and not "cancelled");
    }

    [Fact]
    public async Task Update_ToLost_ClosesLinkedActiveTasks()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - спор",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.TaskJournalItems.Add(new TaskJournalItem
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ObjectName = "Земцов Виктор Александрович - спор",
            Title = "Активная задача по проигранному делу",
            AssigneeUserId = owner.Id,
            AssigneeName = owner.FullName,
            AuthorUserId = owner.Id,
            AuthorName = owner.FullName,
            DueAt = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc),
            Status = "new",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.Update(clientCase.Id, new ClientCase
        {
            ClientId = client.Id,
            Title = "Земцов - спор",
            Status = ClientCaseStatus.Lost,
            ResponsibleUserId = owner.Id,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("done", await db.TaskJournalItems
            .Where(item => item.ClientCaseId == clientCase.Id)
            .Select(item => item.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task BulkUpdate_ChangesOnlyVisibleCases()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var otherResponsible = await CreateUserAsync(db, "other@example.com", "Другой");
        var nextResponsible = await CreateUserAsync(db, "next@example.com", "Новый ответственный");
        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = otherResponsible.Id,
        };
        var visibleByClient = new ClientCase
        {
            Client = visibleClient,
            Title = "Видимое по клиенту",
            Status = ClientCaseStatus.Open,
            Stage = "Подготовка",
            Priority = "normal",
        };
        var visibleByCaseResponsible = new ClientCase
        {
            Client = hiddenClient,
            Title = "Видимое по ответственному дела",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
            Stage = "Подготовка",
            Priority = "high",
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherResponsible.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.ClientCases.AddRange(visibleByClient, visibleByCaseResponsible, hiddenCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.BulkUpdate(new CasesV2Controller.BulkUpdateCasesRequest
        {
            Ids = [visibleByClient.Id, visibleByCaseResponsible.Id, hiddenCase.Id],
            Status = ClientCaseStatus.OnHold,
            ResponsibleUserId = nextResponsible.Id,
            Stage = "Судебное производство",
            Priority = "urgent",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(3, GetAnonymousInt(ok.Value, "requested"));

        await db.Entry(visibleByClient).ReloadAsync();
        await db.Entry(visibleByCaseResponsible).ReloadAsync();
        await db.Entry(hiddenCase).ReloadAsync();

        Assert.Equal(ClientCaseStatus.OnHold, visibleByClient.Status);
        Assert.Equal(nextResponsible.Id, visibleByClient.ResponsibleUserId);
        Assert.Equal("Судебное производство", visibleByClient.Stage);
        Assert.Equal("urgent", visibleByClient.Priority);
        Assert.Equal(ClientCaseStatus.OnHold, visibleByCaseResponsible.Status);
        Assert.Equal(nextResponsible.Id, visibleByCaseResponsible.ResponsibleUserId);
        Assert.Equal("Судебное производство", visibleByCaseResponsible.Stage);
        Assert.Equal("urgent", visibleByCaseResponsible.Priority);
        Assert.Equal(ClientCaseStatus.Open, hiddenCase.Status);
        Assert.Equal(otherResponsible.Id, hiddenCase.ResponsibleUserId);
        Assert.Contains("Массовое изменение", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("статус «В работе» → «Приостановлено»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("ответственный «Ответственный владельца» → «Новый ответственный»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("стадия «Подготовка» → «Судебное производство»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("приоритет «Обычный» → «Критический»", visibleByClient.EventHistory, StringComparison.Ordinal);
        Assert.Contains("ответственный «Владелец» → «Новый ответственный»", visibleByCaseResponsible.EventHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Массовое изменение", hiddenCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_UploadsVersionsListsAndDeletesVisibleCaseFiles()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var firstUpload = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("first", "claim.txt"),
            " Иск ",
            " Подготовлен ",
            documentKey: null,
            ct: CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedResult>(firstUpload.Result);
        var first = Assert.IsType<ClientCaseDocumentDto>(firstCreated.Value);

        Assert.Equal("Иск", first.DocumentType);
        Assert.Equal("Подготовлен", first.Status);
        Assert.Equal("claim.txt", first.OriginalFileName);
        Assert.Equal(1, first.Version);
        Assert.True(first.IsLatestVersion);

        var secondUpload = await controller.UploadDocument(
            clientCase.Id,
            CreateFormFile("second", "claim-v2.txt"),
            "Иск",
            "Отправлен",
            first.DocumentKey,
            CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedResult>(secondUpload.Result);
        var second = Assert.IsType<ClientCaseDocumentDto>(secondCreated.Value);

        Assert.Equal(first.DocumentKey, second.DocumentKey);
        Assert.Equal(2, second.Version);
        Assert.True(second.IsLatestVersion);

        var listResult = await controller.GetDocuments(clientCase.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var documents = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(ok.Value);

        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, item => item.Id == first.Id && !item.IsLatestVersion);
        Assert.Contains(documents, item => item.Id == second.Id && item.IsLatestVersion);

        var deleteResult = await controller.DeleteDocument(clientCase.Id, first.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);
        Assert.DoesNotContain(await db.ClientCaseDocuments.ToListAsync(), item => item.Id == first.Id);
        Assert.Contains(await db.ClientCaseDocuments.ToListAsync(), item => item.Id == second.Id);
        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Документ «claim.txt» загружен", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Документ «claim-v2.txt»: загружена новая версия", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Документ «claim.txt» удален", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentTemplates_ReturnsSupportedCaseDocumentTemplates()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentTemplates(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(ok.Value);

        Assert.Contains(templates, item => item.Key == "case-summary" && item.DocumentType == "Карточка дела");
        Assert.Contains(templates, item => item.Key == "claim" && item.DocumentType == "Иск");
        Assert.Contains(templates, item => item.Key == "court-letter" && item.DocumentType == "Переписка");
    }

    [Fact]
    public async Task DocumentTemplates_CreatePatchAndRenderCustomTemplate()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Dictionaries,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            ViewScope = DataVisibilityScope.All,
        });
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            CourtName = "Арбитражный суд Самарской области",
            ResponsibleUserId = owner.Id,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createResult = await controller.CreateDocumentTemplate(new CasesV2Controller.UpsertCaseDocumentTemplateRequest
        {
            Key = "custom-claim",
            Label = "Рабочий шаблон",
            DocumentType = "Иск",
            Heading = "Заявление",
            Content = "<p>{{case.title}} — {{client.name}} — {{court.name}}</p>",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(created.Value);
        Assert.Equal("custom-claim", createdDto.Key);
        Assert.Equal(1, createdDto.Version);

        var updateResult = await controller.UpdateDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.UpsertCaseDocumentTemplateRequest
            {
                Label = "Обновленный шаблон",
                Content = "<p>{{case.title}} / {{responsible.name}}</p>",
            },
            CancellationToken.None);

        var updatedOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<CasesV2Controller.CaseDocumentTemplateDefinitionDto>(updatedOk.Value);
        Assert.Equal("Обновленный шаблон", updatedDto.Label);
        Assert.Equal(2, updatedDto.Version);

        var listResult = await controller.GetDocumentTemplates(CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.CaseDocumentTemplateDefinitionDto>>(listOk.Value);
        Assert.Contains(templates, item => item.Key == "custom-claim" && item.Label == "Обновленный шаблон");

        var renderResult = await controller.RenderDocumentTemplate(
            "custom-claim",
            new CasesV2Controller.RenderCaseDocumentTemplateRequest { ClientCaseId = clientCase.Id },
            CancellationToken.None);

        var renderOk = Assert.IsType<OkObjectResult>(renderResult.Result);
        var rendered = Assert.IsType<CasesV2Controller.RenderedCaseDocumentTemplateDto>(renderOk.Value);
        Assert.Equal("custom-claim", rendered.TemplateKey);
        Assert.Contains("Земцов - банкротство", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Владелец", rendered.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentTypes_ReturnsTemplateAndStoredTypes()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.ClientCaseDocuments.Add(new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "reconciliation",
            Version = 1,
            DocumentType = "Акт сверки",
            Status = "Подготовлен",
            OriginalFileName = "reconciliation.xlsx",
            StoredFileName = "reconciliation.xlsx",
            StoragePath = "1/reconciliation.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentTypes(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var types = Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
        Assert.Contains("Акт сверки", types);
        Assert.Contains("Иск", types);
        Assert.Contains("Жалоба", types);
        Assert.Contains("Доказательство", types);
        Assert.Contains("Переписка", types);
    }

    [Fact]
    public async Task CaseDocumentStorage_SavesAndProtectsRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var storage = new LocalCaseDocumentStorage(new TestWebHostEnvironment
        {
            ContentRootPath = contentRoot,
        });
        var relativePath = storage.BuildRelativePath(15, "claim.txt");

        await storage.SaveAsync(relativePath, Encoding.UTF8.GetBytes("claim"), CancellationToken.None);

        Assert.True(storage.Exists(relativePath));
        Assert.EndsWith(Path.Combine("App_Data", "case-documents", "15", "claim.txt"), storage.ResolvePath(relativePath));
        Assert.Throws<InvalidOperationException>(() => storage.ResolvePath(Path.Combine("..", "outside.txt")));

        storage.DeleteIfExists(relativePath);
        Assert.False(storage.Exists(relativePath));
    }

    [Fact]
    public async Task GetDocumentById_ReturnsDocumentWhenCaseIsVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var first = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "template-claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim-v1.html",
            StoredFileName = "claim-v1.html",
            StoragePath = "1/claim-v1.html",
            ContentType = "text/html",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var second = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "template-claim",
            Version = 2,
            DocumentType = "Иск",
            Status = "Подписан",
            OriginalFileName = "claim-v2.html",
            StoredFileName = "claim-v2.html",
            StoragePath = "1/claim-v2.html",
            ContentType = "text/html",
            SizeBytes = 2048,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.AddRange(first, second);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var oldVersionResult = await controller.GetDocumentById(first.Id, CancellationToken.None);
        var latestResult = await controller.GetDocumentById(second.Id, CancellationToken.None);

        var oldOk = Assert.IsType<OkObjectResult>(oldVersionResult.Result);
        var oldDto = Assert.IsType<ClientCaseDocumentDto>(oldOk.Value);
        Assert.False(oldDto.IsLatestVersion);
        Assert.Equal("claim", oldDto.TemplateKey);

        var latestOk = Assert.IsType<OkObjectResult>(latestResult.Result);
        var latestDto = Assert.IsType<ClientCaseDocumentDto>(latestOk.Value);
        Assert.True(latestDto.IsLatestVersion);
        Assert.Equal("Подписан", latestDto.Status);
    }

    [Fact]
    public async Task GetDocumentById_ReturnsSourceTaskIdForTaskDocuments()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "task-42-file",
            Version = 1,
            DocumentType = "Документ задачи",
            Status = "Загружен",
            OriginalFileName = "task-file.txt",
            StoredFileName = "task-file.txt",
            StoragePath = "1/task-file.txt",
            ContentType = "text/plain",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentById(document.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal(42, dto.SourceTaskId);
    }

    [Fact]
    public async Task GetDocumentRegistry_ReturnsLatestVisibleDocumentsWithFilters()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var other = await CreateUserAsync(db, "other@example.com", "Другой");
        var visibleClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Скрытый клиент",
            IsActive = true,
            ResponsibleUserId = other.Id,
        };
        var visibleCase = new ClientCase
        {
            Client = visibleClient,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        var hiddenCase = new ClientCase
        {
            Client = hiddenClient,
            Title = "Скрытое дело",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCaseDocuments.AddRange(
            new ClientCaseDocument
            {
                ClientCase = visibleCase,
                DocumentKey = "claim",
                Version = 1,
                DocumentType = "Иск",
                Status = "Подготовлен",
                OriginalFileName = "claim-v1.pdf",
                StoredFileName = "claim-v1.pdf",
                StoragePath = "1/claim-v1.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                UploadedByName = "Владелец",
                UploadedAt = DateTime.UtcNow.AddMinutes(-10),
            },
            new ClientCaseDocument
            {
                ClientCase = visibleCase,
                DocumentKey = "claim",
                Version = 2,
                DocumentType = "Иск",
                Status = "Подписан",
                OriginalFileName = "claim-v2.pdf",
                StoredFileName = "claim-v2.pdf",
                StoragePath = "1/claim-v2.pdf",
                ContentType = "application/pdf",
                SizeBytes = 2048,
                UploadedByName = "Владелец",
                UploadedAt = DateTime.UtcNow,
            },
            new ClientCaseDocument
            {
                ClientCase = hiddenCase,
                DocumentKey = "claim-hidden",
                Version = 1,
                DocumentType = "Иск",
                Status = "Подготовлен",
                OriginalFileName = "claim-hidden.pdf",
                StoredFileName = "claim-hidden.pdf",
                StoragePath = "2/claim-hidden.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                UploadedByName = "Другой",
                UploadedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetDocumentRegistry(
            clientCaseId: null,
            search: "claim",
            documentType: "Иск",
            status: null,
            sourceTaskId: null,
            latestOnly: true,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var property = ok.Value!.GetType().GetProperty("items");
        Assert.NotNull(property);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseDocumentDto>>(property.GetValue(ok.Value));
        var item = Assert.Single(items);
        Assert.Equal("claim-v2.pdf", item.OriginalFileName);
        Assert.True(item.IsLatestVersion);
    }

    [Fact]
    public async Task DownloadDocumentById_ReturnsFileWhenCaseIsVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var storagePath = Path.Combine("15", "claim.txt");
        var fullPath = Path.Combine(contentRoot, "App_Data", "case-documents", storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "claim", Encoding.UTF8);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.txt",
            StoredFileName = "claim.txt",
            StoragePath = storagePath,
            ContentType = "text/plain",
            SizeBytes = 5,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.DownloadDocumentById(document.Id, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(fullPath, file.FileName);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal("claim.txt", file.FileDownloadName);
    }

    [Fact]
    public async Task Documents_GeneratesTemplateFromCaseDataAndStoresVersionedFile()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            CaseNumber = "2026-001",
            CaseType = "Банкротство",
            Stage = "Подготовка заявления",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            Claimant = "Земцов Виктор Александрович",
            Defendant = "Банк",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = owner.Id,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.GenerateDocumentTemplate(
            clientCase.Id,
            new CasesV2Controller.GenerateCaseDocumentTemplateRequest
            {
                TemplateKey = "claim",
                Status = "Подготовлен",
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);

        Assert.Equal("template-claim", dto.DocumentKey);
        Assert.Equal("claim", dto.TemplateKey);
        Assert.Equal("Исковое заявление", dto.TemplateLabel);
        Assert.Equal("Иск", dto.DocumentType);
        Assert.Equal("Подготовлен", dto.Status);
        Assert.Equal(1, dto.Version);
        Assert.EndsWith(".html", dto.OriginalFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html; charset=utf-8", dto.ContentType);
        Assert.True(dto.SizeBytes > 0);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        var fullPath = Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath);
        Assert.True(File.Exists(fullPath));
        var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        Assert.Contains("Исковое заявление", html, StringComparison.Ordinal);
        Assert.Contains("Земцов Виктор Александрович", html, StringComparison.Ordinal);
        Assert.Contains("Арбитражный суд Самарской области", html, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Сформирован шаблон", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDocument_ChangesStatusAndWritesCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.pdf",
            StoredFileName = "claim.pdf",
            StoragePath = "1/claim.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.UpdateDocument(clientCase.Id, document.Id, new CasesV2Controller.UpdateCaseDocumentRequest
        {
            Status = " Отправлен ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal("Отправлен", dto.Status);

        await db.Entry(clientCase).ReloadAsync();
        await db.Entry(document).ReloadAsync();
        Assert.Equal("Отправлен", document.Status);
        Assert.Contains("Документ «claim.pdf»: статус «Подготовлен» → «Отправлен»", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Изменил: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDocumentById_ChangesStatusAndWritesCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim.pdf",
            StoredFileName = "claim.pdf",
            StoragePath = "1/claim.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.UpdateDocumentById(document.Id, new CasesV2Controller.UpdateCaseDocumentRequest
        {
            DocumentType = " Ходатайство ",
            Status = " На подписи ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(ok.Value);
        Assert.Equal("Ходатайство", dto.DocumentType);
        Assert.Equal("На подписи", dto.Status);

        await db.Entry(clientCase).ReloadAsync();
        await db.Entry(document).ReloadAsync();
        Assert.Equal("Ходатайство", document.DocumentType);
        Assert.Equal("На подписи", document.Status);
        Assert.Contains("тип «Иск» → «Ходатайство»", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("статус «Подготовлен» → «На подписи»", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadDocumentVersionById_CreatesNextVersionForExistingDocument()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
        };
        var document = new ClientCaseDocument
        {
            ClientCase = clientCase,
            DocumentKey = "claim",
            Version = 1,
            DocumentType = "Иск",
            Status = "Подготовлен",
            OriginalFileName = "claim-v1.pdf",
            StoredFileName = "claim-v1.pdf",
            StoragePath = "1/claim-v1.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedByName = "Владелец",
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.Add(document);
        await db.SaveChangesAsync();
        var contentRoot = Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N"));
        var controller = CreateController(db, owner, contentRoot);

        var result = await controller.UploadDocumentVersionById(
            document.Id,
            CreateFormFile("second version", "claim-v2.pdf", "application/pdf"),
            documentType: null,
            status: " Подписан ",
            ct: CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<ClientCaseDocumentDto>(created.Value);
        Assert.Equal(document.DocumentKey, dto.DocumentKey);
        Assert.Equal(2, dto.Version);
        Assert.Equal("Иск", dto.DocumentType);
        Assert.Equal("Подписан", dto.Status);
        Assert.True(dto.IsLatestVersion);

        var stored = await db.ClientCaseDocuments.SingleAsync(item => item.Id == dto.Id);
        Assert.Equal(document.DocumentKey, stored.DocumentKey);
        Assert.True(File.Exists(Path.Combine(contentRoot, "App_Data", "case-documents", stored.StoragePath)));

        var oldVersionResult = await controller.GetDocumentById(document.Id, CancellationToken.None);
        var oldOk = Assert.IsType<OkObjectResult>(oldVersionResult.Result);
        var oldDto = Assert.IsType<ClientCaseDocumentDto>(oldOk.Value);
        Assert.False(oldDto.IsLatestVersion);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Документ «claim-v2.pdf»: загружена новая версия", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkActivity_UpdatesLastViewAndConfirmationHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - активность",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.MarkActivity(clientCase.Id, new CasesV2Controller.MarkClientCaseActivityRequest
        {
            Confirmed = true,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Equal(owner.Id, dto.LastViewedByUserId);
        Assert.Equal("Владелец", dto.LastViewedByUserName);
        Assert.Equal(owner.Id, dto.ActivityConfirmedByUserId);
        Assert.Contains("Активность по делу подтверждена", dto.EventHistory, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.NotNull(clientCase.LastViewedAt);
        Assert.NotNull(clientCase.ActivityConfirmedAt);
        Assert.Contains("Подтвердил: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_ReturnsParsedCaseHistoryEntries()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - история",
            EventHistory = """
                18.05.2026 10:30: Создано дело.
                18.05.2026 11:15: Комментарий по делу: Проверить срок.
                """,
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetTimeline(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var timeline = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseTimelineEntryDto>>(ok.Value);

        Assert.Equal(2, timeline.Count);
        Assert.Equal("Комментарий по делу: Проверить срок.", timeline[0].Text);
        Assert.Equal(new DateTime(2026, 5, 18, 11, 15, 0, DateTimeKind.Utc), timeline[0].OccurredAt);
        Assert.Equal("Создано дело.", timeline[1].Text);
    }

    [Fact]
    public async Task AddHistoryEntry_AppendsUserCommentToCaseHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - история",
            Status = ClientCaseStatus.Open,
            EventHistory = "Дело создано",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.AddHistoryEntry(clientCase.Id, new CasesV2Controller.AddClientCaseHistoryEntryRequest
        {
            Text = " Нужно проверить судебные сроки ",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Contains("Комментарий по делу: Нужно проверить судебные сроки. Автор: Владелец.", dto.EventHistory, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Дело создано", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Комментарий по делу: Нужно проверить судебные сроки. Автор: Владелец.", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkActivity_DoesNotResetLastViewForNonResponsibleViewer()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель", DataVisibilityScope.All);
        var viewedAt = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - активность",
            Status = ClientCaseStatus.Open,
            LastViewedAt = viewedAt,
            LastViewedByUserId = owner.Id,
            LastViewedByUserName = owner.FullName,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.MarkActivity(clientCase.Id, new CasesV2Controller.MarkClientCaseActivityRequest
        {
            Confirmed = false,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseActivityDto>(ok.Value);
        Assert.Equal(viewedAt, dto.LastViewedAt);
        Assert.Equal(owner.Id, dto.LastViewedByUserId);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Equal(viewedAt, clientCase.LastViewedAt);
        Assert.Equal(owner.Id, clientCase.LastViewedByUserId);
    }

    [Fact]
    public async Task RunCourtSync_RecordsManualCourtCheckInHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - суд",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            CourtUrl = "https://kad.arbitr.ru/card/123",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RunCourtSync(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);

        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("manual-check-recorded", dto.Status);
        Assert.True(dto.HasCourtBinding);
        Assert.Contains("Внешняя синхронизация пока не подключена", dto.Message, StringComparison.Ordinal);
        Assert.Equal("Владелец", dto.CheckedByUserName);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Ручная проверка судебного источника зафиксирована", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Инициатор: Владелец", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CourtSyncLogs_ReturnManualCheckHistory()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - суд",
            CourtName = "Арбитражный суд Самарской области",
            CourtCaseNumber = "А55-12345/2026",
            EventHistory = """
                18.05.2026 10:30: Ручная проверка судебного источника зафиксирована. Инициатор: Владелец.
                18.05.2026 10:35: Комментарий по делу: обычная история.
                """,
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.GetCourtSyncLogs(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var logs = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseCourtSyncLogDto>>(ok.Value);
        var log = Assert.Single(logs);

        Assert.Equal("manual-check-recorded", log.Status);
        Assert.Equal("Владелец", log.ActorName);
        Assert.Contains("Ручная проверка судебного источника зафиксирована", log.Message, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 5, 18, 10, 30, 0, DateTimeKind.Utc), log.OccurredAt);
    }

    [Fact]
    public async Task RunCourtSync_ExplainsMissingCourtBinding()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - без суда",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var result = await controller.RunCourtSync(clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(ok.Value);

        Assert.Equal("binding-required", dto.Status);
        Assert.False(dto.HasCourtBinding);
        Assert.Contains("не указан номер дела в суде", dto.Message, StringComparison.Ordinal);

        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Ручная проверка судебного источника не выполнена", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_AddListsAndDeletesDirectoryLink()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var relatedClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
            Phone = "89990000000",
            Email = "zemcov@example.com",
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var createdResult = await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Свидетель",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createdResult.Result);
        var createdDto = Assert.IsType<ClientCaseRelatedPartyDto>(created.Value);
        Assert.Equal("client", createdDto.SourceType);
        Assert.Equal(relatedClient.Id, createdDto.SourceId);
        Assert.Equal("Земцов Виктор Александрович", createdDto.DisplayName);
        Assert.Equal("Свидетель", createdDto.Role);

        var listResult = await controller.GetRelatedParties(clientCase.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(ok.Value);
        var item = Assert.Single(items);
        Assert.Equal(createdDto.Id, item.Id);

        var deleteResult = await controller.DeleteRelatedParty(clientCase.Id, createdDto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);
        Assert.Empty(await db.ClientCaseRelatedParties.AsNoTracking().ToListAsync());
        await db.Entry(clientCase).ReloadAsync();
        Assert.Contains("Связь с делом добавлена: Земцов Виктор Александрович (Свидетель).", clientCase.EventHistory, StringComparison.Ordinal);
        Assert.Contains("Связь с делом удалена: Земцов Виктор Александрович.", clientCase.EventHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_AutoAddsVisibleOrganizationForLinkedPerson()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var organization = new LegalEntity
        {
            ShortName = "Нотариальная палата",
            FullName = "Нотариальная палата Самарской области",
            Director = "Иванов Иван Иванович",
            ResponsibleUserId = owner.Id,
        };
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var relatedClient = new Client
        {
            Name = "Петров Петр Петрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
            LegalEntity = organization,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Нотариус",
        }, CancellationToken.None);

        var listResult = await controller.GetRelatedParties(clientCase.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ClientCaseRelatedPartyDto>>(ok.Value);
        Assert.Contains(items, item => item.SourceType == "client" && item.DisplayName == "Петров Петр Петрович");
        var organizationItem = Assert.Single(items, item => item.SourceType == "legalEntity");
        Assert.Equal(organization.Id, organizationItem.SourceId);
        Assert.Equal("Нотариальная палата", organizationItem.DisplayName);
        Assert.Equal("Организация участника", organizationItem.Role);
        Assert.Contains("Руководитель: Иванов Иван Иванович", organizationItem.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedParties_ForbidsAddWithoutEditPermission()
    {
        await using var db = await CreateContextAsync();
        var viewer = await CreateUserAsync(db, "viewer@example.com", "Наблюдатель");
        var permission = await db.RolePermissions.FirstAsync(item => item.RoleId == viewer.RoleId && item.Section == RolePermissionCatalog.Clients);
        permission.CanEdit = false;
        var client = new Client
        {
            Name = "Основной клиент",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var relatedClient = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = viewer.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(client, relatedClient);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, viewer);

        var result = await controller.AddRelatedParty(clientCase.Id, new CasesV2Controller.UpsertClientCaseRelatedPartyRequest
        {
            SourceType = "client",
            SourceId = relatedClient.Id,
            Role = "Свидетель",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.ClientCaseRelatedParties.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Conversation_AddsInternalMessageAndReturnsOnlyConversationEntries()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db, "owner@example.com", "Владелец");
        var client = new Client
        {
            Name = "Земцов Виктор Александрович",
            IsActive = true,
            ResponsibleUserId = owner.Id,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Земцов - банкротство",
            Status = ClientCaseStatus.Open,
            EventHistory = "18.05.2026 09:00: Статус дела изменен."
                + Environment.NewLine
                + "18.05.2026 09:10: Комментарий по делу: Проверить документы. Автор: Администратор.",
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner);

        var addResult = await controller.AddConversationEntry(clientCase.Id, new CasesV2Controller.AddClientCaseConversationEntryRequest
        {
            Text = "Коллеги, нужно уточнить срок подачи.\nОтветственный - юрист.",
        }, CancellationToken.None);

        var addOk = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(addOk.Value);
        Assert.Equal(2, addedItems.Count);
        Assert.Contains(addedItems, item => item.Kind == "comment" && item.Text == "Проверить документы");
        var newMessage = Assert.Single(addedItems, item => item.Kind == "message");
        Assert.Equal("Коллеги, нужно уточнить срок подачи. Ответственный - юрист", newMessage.Text);
        Assert.Equal("Владелец", newMessage.AuthorName);

        var getResult = await controller.GetConversation(clientCase.Id, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var getItems = Assert.IsAssignableFrom<IReadOnlyList<CasesV2Controller.ClientCaseConversationEntryDto>>(getOk.Value);
        Assert.Equal(2, getItems.Count);
        Assert.DoesNotContain(getItems, item => item.Text.Contains("Статус дела", StringComparison.Ordinal));
    }

    private static IReadOnlyList<ClientCase> GetAnonymousItems(object? value)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty("items");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<ClientCase>>(property.GetValue(value));
    }

    private static int GetAnonymousInt(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(value));
    }

    private static CasesV2Controller CreateController(PaymentContext db, User user, string? contentRootPath = null)
        => new(db, new TestWebHostEnvironment
        {
            ContentRootPath = contentRootPath ?? Path.Combine(Path.GetTempPath(), "payplanner-case-documents", Guid.NewGuid().ToString("N")),
        })
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.FullName),
                        new Claim(ClaimTypes.Email, user.Email),
                    ], "TestAuth")),
                },
            },
        };

    private static IFormFile CreateFormFile(string content, string fileName, string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        string fullName,
        string viewScope = DataVisibilityScope.Own)
    {
        var role = new Role
        {
            Name = $"case-v2-{Guid.NewGuid():N}",
            Description = "Пользователь дел",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Clients,
                    CanView = true,
                    CanCreate = true,
                    CanEdit = true,
                    CanDelete = true,
                    CanExport = true,
                    ViewScope = viewScope,
                },
            ],
        };
        var user = new User
        {
            Email = email,
            FullName = fullName,
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
