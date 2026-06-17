using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class RolePermissionsAuditTests
{
    [Fact]
    public async Task GetAudit_ReturnsReadyRole_WhenCatalogPermissionsAreComplete()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "Legal team" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        db.Users.Add(new User
        {
            Email = "lawyer@example.com",
            PasswordHash = "hash",
            FullName = "Lawyer",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles);
        Assert.Equal(1, report.TotalRoles);
        Assert.Equal(1, report.ReadyRoles);
        Assert.Equal(0, report.RolesWithIssues);
        Assert.Equal(0, report.RolesWithBroadDataScope);
        Assert.False(roleAudit.HasIssues);
        Assert.Equal(RolePermissionCatalog.AllSections.Length, roleAudit.ConfiguredSectionsCount);
        Assert.Equal(1, roleAudit.AssignedUsersCount);
        Assert.Empty(roleAudit.MissingSections);
        Assert.Empty(roleAudit.UnknownSections);
        Assert.Empty(roleAudit.DuplicateSections);
        Assert.Empty(roleAudit.InvalidViewScopeSections);
    }

    [Fact]
    public async Task GetAudit_FlagsIncompleteLegacyAndUnsafePermissions()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "legacy-lawyer", Description = "Imported role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = "global",
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = false,
                CanEdit = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = "legacySection",
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Audit,
                CanView = true,
                CanCreate = true,
                ViewScope = DataVisibilityScope.All,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles);
        Assert.Equal(0, report.ReadyRoles);
        Assert.Equal(1, report.RolesWithIssues);
        Assert.Equal(1, report.RolesWithBroadDataScope);
        Assert.True(roleAudit.HasIssues);
        Assert.Contains(RolePermissionCatalog.Calendar, roleAudit.MissingSections);
        Assert.Contains("legacySection", roleAudit.UnknownSections);
        Assert.Contains("clients:global", roleAudit.InvalidViewScopeSections);
        Assert.Contains(RolePermissionCatalog.Tasks, roleAudit.CapabilityWithoutViewSections);
        Assert.Contains(RolePermissionCatalog.Audit, roleAudit.RestrictedCapabilitySections);
        Assert.Contains(RolePermissionCatalog.Cases, roleAudit.BroadDataScopeSections);
    }

    [Fact]
    public async Task ExportAuditCsv_ReturnsRoleAuditRows()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "Legal team" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.ExportAuditCsv(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.EndsWith(".csv", file.FileDownloadName);

        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("\"RoleId\";\"RoleName\";\"IsSystemRole\"", csv);
        Assert.Contains($"\"{role.Id}\";\"lawyer\";\"false\"", csv);
    }

    [Fact]
    public async Task GetAudit_ForbidsUserWithoutRoleViewPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "No role access" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "viewer@example.com",
            PasswordHash = "hash",
            FullName = "Viewer",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var response = await controller.GetAudit(CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Update_SynchronizesSpecificCalendarVisibleUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var first = new User
        {
            Email = "calendar-first@example.com",
            PasswordHash = "hash",
            FullName = "Первый сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var second = new User
        {
            Email = "calendar-second@example.com",
            PasswordHash = "hash",
            FullName = "Второй сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                CanViewAnalytics = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { first.Id, first.Id, second.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var storedIds = await db.RoleCalendarVisibleUsers
            .AsNoTracking()
            .Where(item => item.RoleId == role.Id)
            .OrderBy(item => item.UserId)
            .Select(item => item.UserId)
            .ToListAsync();
        Assert.Equal(new[] { first.Id, second.Id }, storedIds);

        request.Calendar.VisibleUserIds = new List<int> { second.Id };
        updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var response = await controller.Get(role.Id, CancellationToken.None);
        var dto = AssertOk<RolePermissionsDto>(response);
        Assert.Equal(DataVisibilityScope.Specific, dto.Calendar.ViewScope);
        Assert.Equal(new[] { second.Id }, dto.Calendar.VisibleUserIds);
    }

    [Fact]
    public async Task Update_NotifiesRoleUsersWhenSpecificCalendarVisibleUsersChanged()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary-notified", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var first = new User
        {
            Email = "calendar-notify-first@example.com",
            PasswordHash = "hash",
            FullName = "Первый сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var second = new User
        {
            Email = "calendar-notify-second@example.com",
            PasswordHash = "hash",
            FullName = "Второй сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();
        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
        {
            RoleId = role.Id,
            UserId = first.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, CreateAdminPrincipal(), notificationService);
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { second.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var notification = Assert.Single(notificationService.CalendarAccessChanges);
        Assert.Equal(role.Id, notification.RoleId);
        Assert.Equal([second.Id], notification.AddedVisibleUserIds);
        Assert.Equal([first.Id], notification.RemovedVisibleUserIds);
    }

    [Fact]
    public async Task Update_RejectsInactiveCalendarVisibleUser()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var inactive = new User
        {
            Email = "calendar-inactive-role@example.com",
            PasswordHash = "hash",
            FullName = "Неактивный сотрудник",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
        };
        db.Users.Add(inactive);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { inactive.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(updateResult);
        Assert.Empty(await db.RoleCalendarVisibleUsers.AsNoTracking().ToListAsync());
    }

    private static RolePermissionsController CreateController(
        PaymentContext db,
        ClaimsPrincipal user,
        INotificationService? notificationService = null)
        => new(db, new RoleManagementAccessService(db), notificationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };

    private static ClaimsPrincipal CreateAdminPrincipal()
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Name, "Admin"),
                new Claim(ClaimTypes.Role, "admin"),
            },
            "test"));

    private static ClaimsPrincipal CreateUserPrincipal(int userId)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "Viewer"),
            },
            "test"));

    private static T AssertOk<T>(ActionResult<T> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<T>(ok.Value);
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

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(int RoleId, IReadOnlyCollection<int> AddedVisibleUserIds, IReadOnlyCollection<int> RemovedVisibleUserIds)> CalendarAccessChanges { get; } = [];

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannelDto>>(Array.Empty<NotificationChannelDto>());

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(PayPlanner.Api.Models.Requests.UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(PayPlanner.Api.Models.Requests.UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationTemplateDto>>(Array.Empty<NotificationTemplateDto>());

        public Task<NotificationTemplateDto> UpdateTemplateAsync(string key, PayPlanner.Api.Models.Requests.UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationMessage> RenderTemplateAsync(string key, string fallbackSubject, string fallbackBody, IReadOnlyDictionary<string, string?> values, bool isSensitive = false, string purpose = NotificationPurposes.ManualTest, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose));

        public Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(int userId, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> SendAsync(NotificationRecipient recipient, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<NotificationSettingsDto> UpsertSenderAsync(PayPlanner.Api.Models.Requests.UpsertNotificationSenderRequest request, int? senderId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(TaskJournalItem task, string actorName, int actorUserId, string action, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task NotifyCalendarAccessChangedAsync(int roleId, IReadOnlyCollection<int> addedVisibleUserIds, IReadOnlyCollection<int> removedVisibleUserIds, string actorName, int actorUserId, CancellationToken cancellationToken = default)
        {
            CalendarAccessChanges.Add((roleId, addedVisibleUserIds.ToArray(), removedVisibleUserIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task NotifyExternalCalendarSyncWarningAsync(string providerName, string operation, string summary, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteMentionAsync(int noteId, int boardId, string boardTitle, string noteText, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteAssignedAsync(int noteId, int boardId, string boardTitle, string noteText, int assignedUserId, string actorName, int actorUserId, DateTime? reminderAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteReminderAsync(StickyNote note, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteImportantChangeAsync(int noteId, int boardId, string boardTitle, string noteText, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, int noteVersion, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentAssignedAsync(Payment payment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentChangedAsync(Payment payment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceCreatedAsync(Payment invoicePayment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceChangedAsync(Payment invoicePayment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActCreatedAsync(Act act, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActChangedAsync(Act act, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractCreatedAsync(Contract contract, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractExpiringAsync(Contract contract, int daysLeft, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseCourtEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseProceduralDeadlineAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessRequestedAsync(ClientCase clientCase, string requesterName, int requesterUserId, string? reason, DateTime requestedAt, IReadOnlyCollection<int> managerUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessResolvedAsync(ClientCase clientCase, bool granted, string actorName, int actorUserId, int requestedByUserId, string requestedByUserName, string? requestReason, string? comment, DateTime resolvedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
