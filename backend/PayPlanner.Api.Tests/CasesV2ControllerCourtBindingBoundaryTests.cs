using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtBind_ForeignContextIsPreservedWithoutProvider(bool postgres, bool ambient)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var before = await CourtBindingStateAsync(db);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        if (!ambient) clientCase.Title = "Несохранённая работа вызывающего кода";
        var provider = new CourtBindingProvider(() => Task.CompletedTask);
        var response = await CourtCoreController(db, user, provider, new CourtCoreStorage(), NewCourtBindingNotifications(db), new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Equal(0, provider.Calls);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(before, await CourtBindingStateAsync(db));
        if (!ambient)
        {
            Assert.True(db.ChangeTracker.HasChanges());
            Assert.Equal("Несохранённая работа вызывающего кода", clientCase.Title);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtBind_UnknownCommitRetainsAcceptedCoreAndRetryDoesNotDuplicate(bool postgres)
    {
        var fault = new CourtBindingCommitFault();
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var first = true;
        var provider = new CourtBindingProvider(() => { if (first) fault.Armed = true; first = false; return Task.CompletedTask; });
        var storage = new CourtCoreStorage();
        var controller = CourtCoreController(db, user, provider, storage, NewCourtBindingNotifications(db), new CourtCoreRealtime(db));
        await Assert.ThrowsAsync<IOException>(() => controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default));
        Assert.True(fault.Called);
        Assert.Equal("https://court.test/bound", (await db.ClientCases.AsNoTracking().SingleAsync()).CourtUrl);
        Assert.Single(await db.ClientCaseCourtInstances.AsNoTracking().ToArrayAsync());
        var events = await db.ClientCaseCourtEvents.CountAsync();
        var deadlines = await db.ClientCaseProceduralDeadlines.CountAsync();
        var tasks = await db.TaskJournalItems.CountAsync();
        Assert.True(events > 0 && deadlines > 0 && tasks > 0);
        Assert.Equal(0, storage.Saves);
        var retry = await CourtCoreController(db, user, provider, storage, NewCourtBindingNotifications(db), new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<OkObjectResult>(retry.Result);
        Assert.Equal(events, await db.ClientCaseCourtEvents.CountAsync());
        Assert.Equal(deadlines, await db.ClientCaseProceduralDeadlines.CountAsync());
        Assert.Equal(tasks, await db.TaskJournalItems.CountAsync());
        Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtBind_PostCommitFailureKeepsBindingAndControls(bool postgres, bool notificationFailure)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtBindingProvider(() => Task.CompletedTask);
        var storage = new CourtCoreStorage { Throw = !notificationFailure };
        var notifications = NewCourtBindingNotifications(db);
        ((CourtBindingNotifications)(object)notifications).Throw = notificationFailure;
        var controller = CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db));
        if (notificationFailure) await Assert.ThrowsAsync<CourtCoreNotificationFault>(() => controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default));
        else await Assert.ThrowsAsync<CourtCoreFileFault>(() => controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default));
        var accepted = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("https://court.test/bound", accepted.CourtUrl);
        Assert.Contains("Привязана судебная карточка", accepted.EventHistory);
        Assert.Null(accepted.CourtSyncLastSuccessAt);
        Assert.Single(await db.ClientCaseCourtInstances.ToArrayAsync());
        var deadlines = await db.ClientCaseProceduralDeadlines.CountAsync();
        var tasks = await db.TaskJournalItems.CountAsync();
        Assert.True(deadlines > 0 && tasks > 0);
        storage.Throw = false;
        ((CourtBindingNotifications)(object)notifications).Throw = false;
        var retry = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<OkObjectResult>(retry.Result);
        Assert.Equal(deadlines, await db.ClientCaseProceduralDeadlines.CountAsync());
        Assert.Equal(tasks, await db.TaskJournalItems.CountAsync());
        Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtBind_ReplyContainsOnlyCurrentCaseActiveComplaints(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var other = new ClientCase { Title = "Другое дело", ResponsibleUserId = user.Id, ClientId = clientCase.ClientId };
        var instance = new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "first" };
        db.ClientCases.Add(other); db.ClientCaseCourtInstances.Add(instance);
        await db.SaveChangesAsync();
        db.ClientCaseCourtComplaints.AddRange(
            new ClientCaseCourtComplaint { ClientCaseId = clientCase.Id, CourtInstanceId = instance.Id, Title = "Своя жалоба" },
            new ClientCaseCourtComplaint { ClientCaseId = other.Id, CourtInstanceId = instance.Id, Title = "Чужая жалоба", Notes = "private-other-case" },
            new ClientCaseCourtComplaint { ClientCaseId = clientCase.Id, CourtInstanceId = instance.Id, Title = "Архивная жалоба", IsArchived = true });
        await db.SaveChangesAsync();
        var controller = CourtCoreController(db, user, new CourtBindingProvider(() => Task.CompletedTask), new CourtCoreStorage(), NewCourtBindingNotifications(db), new CourtCoreRealtime(db));
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(
            (await controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default)).Result).Value);
        Assert.Equal("Своя жалоба", Assert.Single(dto.CourtInstance.Complaints).Title);
        Assert.Equal(3, await db.ClientCaseCourtComplaints.CountAsync());
        Assert.Equal("private-other-case", await db.ClientCaseCourtComplaints.Where(row => row.ClientCaseId == other.Id).Select(row => row.Notes).SingleAsync());
    }

    [Fact]
    public async Task CourtBind_ExistingPostgresBusyInstanceRefusesThenRetrySucceeds()
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var competitor = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "first" });
        await db.SaveChangesAsync();
        var before = await CourtBindingStateAsync(db);
        var first = true;
        var provider = new CourtBindingProvider(async () =>
        {
            if (!first) return;
            first = false;
            await competitor.Database.BeginTransactionAsync();
            await competitor.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtName, "Занятая инстанция"));
        });
        var storage = new CourtCoreStorage();
        var rejected = await CourtCoreController(db, user, provider, storage, NewCourtBindingNotifications(db), new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<ConflictObjectResult>(rejected.Result);
        Assert.Equal(before, await CourtBindingStateAsync(db));
        Assert.Equal(0, storage.Saves);
        await competitor.Database.RollbackTransactionAsync();
        var retry = await CourtCoreController(db, user, provider, storage, NewCourtBindingNotifications(db), new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<OkObjectResult>(retry.Result);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, storage.Saves);
    }

    [Fact]
    public void CourtBind_InstanceSnapshotCoversConfirmedSourceFields()
    {
        var type = typeof(CasesV2Controller).GetNestedType("CourtBindingInstanceState", BindingFlags.NonPublic)!;
        var capture = type.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var field in new[] { "Id", "ClientCaseId", "Instance", "CourtId", "CourtName", "CourtCaseNumber", "JudicialUid", "CourtUrl", "JudgeName" })
        {
            var initial = capture.Invoke(null, [new ClientCaseCourtInstance()]);
            var changed = new ClientCaseCourtInstance();
            var property = typeof(ClientCaseCourtInstance).GetProperty(field)!;
            property.SetValue(changed, property.PropertyType == typeof(string) ? "Изменено" : 17);
            Assert.NotEqual(initial, capture.Invoke(null, [changed]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtBind_LongSourceUrlKeepsExistingReaderBoundary(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var source = "https://court.test/" + new string('x', 1100);
        var provider = new CourtBindingProvider(() => Task.CompletedTask);
        var controller = CourtCoreController(db, user, provider, new CourtCoreStorage(), NewCourtBindingNotifications(db), new CourtCoreRealtime(db));
        var response = await controller.BindCourtSyncCase(clientCase.Id, new CasesV2Controller.ClientCaseCourtSyncBindRequest
        { Instance = "first", SourceUrl = source, ConfirmDifferences = true }, default);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(source[..1000], provider.LastBinding!.SourceUrl);
        Assert.Equal(source[..1000], dto.CourtUrl);
        Assert.Equal(source[..1000], (await db.ClientCases.AsNoTracking().SingleAsync()).CourtUrl);
    }

    private sealed class CourtBindingCommitFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Armed) return Task.CompletedTask;
            Armed = false; Called = true;
            throw new IOException("Synthetic lost binding COMMIT acknowledgement.");
        }
    }
}
