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

    [Fact]
    public async Task EnsureGeneratedPaymentDetailsAsync_CreatesMissingPaymentForCompletedResultTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author@example.com", "Author");
        var assignee = await CreateUserAsync(db, "contractor@example.com", "Contractor");
        var client = new Client { Name = "Client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var completedAt = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskJournalItem
        {
            Title = "Accepted work",
            Status = "done",
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            DueAt = completedAt,
            UpdatedAt = completedAt,
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 3_500m,
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var changed = await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(db);

        Assert.Equal(1, changed);
        var fixedTask = await db.TaskJournalItems
            .Include(item => item.ContractorPayment!)
                .ThenInclude(payment => payment.Responsibles)
            .SingleAsync(item => item.Id == task.Id);
        var payment = Assert.IsType<Payment>(fixedTask.ContractorPayment);
        Assert.Equal(PaymentType.Expense, payment.Type);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(3_500m, payment.Amount);
        Assert.Equal(fixedTask.UpdatedAt!.Value.Date.AddDays(5), payment.Date);
        Assert.Equal(client.Id, payment.ExpenseClientId);
        Assert.Equal("Contractor", payment.CounterpartyName);
        Assert.Contains(payment.Responsibles, item => item.UserId == assignee.Id);
        Assert.Contains($"TaskContractorPayment]:{task.Id}", payment.SystemNotes, StringComparison.Ordinal);

        var secondRunChanged = await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(db);
        Assert.Equal(0, secondRunChanged);
    }

    [Fact]
    public async Task LegacyExpensePaymentRecipientBackfill_RecognizesExternalExecutorFromDescriptionPrefix()
    {
        await using var db = await CreateContextAsync();
        var gorbunov = await CreateUserAsync(db, "gorbunov@example.com", "\u0413\u043e\u0440\u0431\u0443\u043d\u043e\u0432 \u0410\u043b\u0435\u043a\u0441\u0435\u0439");
        var natalia = await CreateUserAsync(db, "natalia@example.com", "\u041a\u0443\u0437\u044c\u043c\u0438\u043d\u043e\u0432\u0430 \u041d\u0430\u0442\u0430\u043b\u044c\u044f \u0418\u0433\u043e\u0440\u0435\u0432\u043d\u0430");
        var editor = await CreateUserAsync(db, "editor@example.com", "Editor");
        editor.IsExternalContractor = false;
        await db.SaveChangesAsync();

        var gorbunovPayment = new Payment
        {
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Amount = 2_500m,
            Date = new DateTime(2026, 3, 30),
            PlannedDate = new DateTime(2026, 3, 30),
            Description = "\u0413\u041e\u0420\u0411\u0423\u041d\u041e\u0412 - \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043a\u0430 \u0438\u0441\u043a\u0430",
            CreatedAt = DateTime.UtcNow,
        };
        gorbunovPayment.Responsibles.Add(new PaymentResponsible { UserId = editor.Id });

        var nataliaPayment = new Payment
        {
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Amount = 2_000m,
            Date = new DateTime(2026, 3, 31),
            PlannedDate = new DateTime(2026, 3, 31),
            Description = "\u041d\u0410\u0422\u0410\u041b\u042c\u042f \u042e\u0420\u0418\u0421\u0422 - \u0438\u0441\u043a",
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.AddRange(gorbunovPayment, nataliaPayment);
        await db.SaveChangesAsync();

        var changed = await LegacyExpensePaymentRecipientBackfillService.EnsureRecognizedRecipientsAsync(db);

        Assert.Equal(4, changed);
        var fixedGorbunovPayment = await db.Payments
            .Include(payment => payment.Responsibles)
            .SingleAsync(payment => payment.Id == gorbunovPayment.Id);
        Assert.Equal(gorbunov.FullName, fixedGorbunovPayment.CounterpartyName);
        Assert.Equal(DocumentKind.Invoice, fixedGorbunovPayment.DocumentKind);
        Assert.Contains(fixedGorbunovPayment.Responsibles, item => item.UserId == gorbunov.Id);
        Assert.Contains(fixedGorbunovPayment.Responsibles, item => item.UserId == editor.Id);

        var fixedNataliaPayment = await db.Payments
            .Include(payment => payment.Responsibles)
            .SingleAsync(payment => payment.Id == nataliaPayment.Id);
        Assert.Equal(natalia.FullName, fixedNataliaPayment.CounterpartyName);
        Assert.Contains(fixedNataliaPayment.Responsibles, item => item.UserId == natalia.Id);

        var secondRunChanged = await LegacyExpensePaymentRecipientBackfillService.EnsureRecognizedRecipientsAsync(db);
        Assert.Equal(0, secondRunChanged);
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
