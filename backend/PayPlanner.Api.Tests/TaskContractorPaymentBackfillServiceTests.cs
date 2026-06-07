using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class TaskContractorPaymentBackfillServiceTests
{
    [Fact]
    public async Task EnsureGeneratedPaymentDetailsAsync_FillsExecutorAndCaseLinks_ForLegacyTaskPayment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author@example.com", "Постановщик");
        var assignee = await CreateUserAsync(db, "contractor@example.com", "Исполнитель Иван");
        var client = new Client { Name = "Клиент по делу" };
        var legalEntity = new LegalEntity { ShortName = "ООО Компания" };
        db.Clients.Add(client);
        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();

        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Дело клиента",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 2_000m,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            Description = "Оплата исполнителю по задаче",
            Notes = "Автоматически создано из задачи.",
            CounterpartyName = "",
            ClientId = client.Id,
            LegalEntityId = legalEntity.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            Title = "Подготовить документы",
            Status = "done",
            ClientId = client.Id,
            LegalEntityId = legalEntity.Id,
            ClientCaseId = clientCase.Id,
            AssigneeUserId = assignee.Id,
            AssigneeName = "  Исполнитель Иван  ",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 2_000m,
            ContractorPaymentId = payment.Id,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        payment.SystemNotes = $"[PayPlanner:TaskContractorPayment]:{task.Id}";
        await db.SaveChangesAsync();

        var changed = await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(db);

        Assert.Equal(1, changed);
        var fixedPayment = await db.Payments
            .Include(item => item.Responsibles)
            .SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(PaymentType.Expense, fixedPayment.Type);
        Assert.Equal("Исполнитель Иван", fixedPayment.CounterpartyName);
        Assert.Null(fixedPayment.ClientId);
        Assert.Null(fixedPayment.LegalEntityId);
        Assert.Equal(client.Id, fixedPayment.ExpenseClientId);
        Assert.Equal(legalEntity.Id, fixedPayment.ExpenseLegalEntityId);
        Assert.Equal(clientCase.Id, fixedPayment.ClientCaseId);
        Assert.Contains(fixedPayment.Responsibles, item => item.UserId == assignee.Id);

        var secondRunChanged = await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(db);
        Assert.Equal(0, secondRunChanged);
    }

    [Fact]
    public async Task EnsureGeneratedPaymentDetailsAsync_DoesNotChangeManualPaymentLinkedToTask()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateUserAsync(db, "contractor@example.com", "Исполнитель Иван");
        var payment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 2_000m,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            Description = "Ручной платеж",
            Notes = "Создан вручную.",
            CounterpartyName = "",
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.TaskJournalItems.Add(new TaskJournalItem
        {
            Title = "Подготовить документы",
            Status = "done",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 2_000m,
            ContractorPaymentId = payment.Id,
        });
        await db.SaveChangesAsync();

        var changed = await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(db);

        Assert.Equal(0, changed);
        var untouchedPayment = await db.Payments.SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(PaymentType.Income, untouchedPayment.Type);
        Assert.Equal("", untouchedPayment.CounterpartyName);
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string fullName)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "task-payment-backfill-test")
            ?? new Role { Name = "task-payment-backfill-test", Description = "Task payment backfill test" };
        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            IsActive = true,
            IsApproved = true,
            IsExternalContractor = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
