using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxQuotaPostgreSqlTests
{
    [Theory]
    [InlineData("participant", true)]
    [InlineData("department", true)]
    [InlineData("all", true)]
    [InlineData("admin", true)]
    [InlineData("foreign", false)]
    [InlineData("sales", false)]
    public async Task ShortTaskMessageUsesFreshDatabaseVisibilityOnPostgreSql(string scope, bool allowed)
    {
        await using var lease = await databases.CreateDatabaseAsync("pp_max_task_access_");
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var settings = new FixedMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var role = new Role { Name = scope == "admin" ? "admin" : "max-reader" };
        var user = new User { Email = "max-reader@fixture.local", FullName = "MAX reader", PasswordHash = "unused", Role = role, IsActive = true, IsApproved = true, Department = "same department" };
        var colleague = new User { Email = "colleague@fixture.local", FullName = "Colleague", PasswordHash = "unused", Role = role, IsActive = true, IsApproved = true, Department = "same department" };
        db.Users.AddRange(user, colleague);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Tasks,
            CanView = true,
            ViewScope = scope == "department" ? DataVisibilityScope.Department : scope == "all" ? DataVisibilityScope.All : DataVisibilityScope.Own
        });
        var task = new TaskJournalItem
        {
            Title = "Разрешённая задача PostgreSQL",
            ObjectName = "Synthetic",
            DueAt = clock.Now.UtcDateTime.AddDays(1),
            AssigneeUserId = colleague.Id,
            SalesRequestId = scope == "sales" ? 901 : null
        };
        if (scope == "sales") task.AssigneeUserId = user.Id;
        if (scope == "participant") task.Participants.Add(new TaskJournalParticipant { UserId = user.Id, DisplayName = user.FullName });
        db.TaskJournalItems.Add(task);
        db.MaxRecipientLinks.Add(new MaxRecipientLink
        {
            UserId = user.Id,
            MaxUserId = 7001,
            Status = MaxRecipientLinkStatuses.Verified,
            VerifiedAt = clock.Now.UtcDateTime,
            LinkTokenHash = Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync();
        var queued = await new MaxOutboxService(db, settings, protection, clock).QueueAsync(new MaxQueueRequest(
            user.Id, MaxMessengerScenarios.Notifications, new string('я', 240), "ignored",
            "Открыть задачу", $"https://crm.example.test/tasks/{task.Id}", UserNotificationTypes.TaskAssigned), CancellationToken.None);
        db.ChangeTracker.Clear();
        var api = new BlockingMaxApi();
        api.Release.TrySetResult();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" }).Build();
        Assert.Equal(1, await new MaxOutboxProcessor(db, settings, api, protection, clock, NullLogger<MaxOutboxProcessor>.Instance, configuration).ProcessBatchAsync(CancellationToken.None));
        var stored = await db.MaxOutboxMessages.AsNoTracking().SingleAsync(x => x.Id == queued.Id);
        Assert.Equal(allowed ? 1 : 0, api.Calls);
        Assert.Equal(allowed ? MaxOutboxStatuses.Sent : MaxOutboxStatuses.Cancelled, stored.Status);
    }
}
