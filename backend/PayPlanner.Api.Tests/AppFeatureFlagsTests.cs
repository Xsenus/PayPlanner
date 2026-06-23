using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public class AppFeatureFlagsTests
{
    [Fact]
    public void FromOptions_MapsLegalFeatureFlags()
    {
        var dto = AppFeatureFlagsDto.FromOptions(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCourtSync = true,
                ExternalCalendarSync = false,
                CourtEvents = true,
                DocumentTemplateLibrary = true,
            },
        });

        Assert.True(dto.Legal.ExternalCourtSync);
        Assert.False(dto.Legal.ExternalCalendarSync);
        Assert.True(dto.Legal.CourtEvents);
        Assert.True(dto.Legal.DocumentTemplateLibrary);
    }

    [Fact]
    public void Get_ReturnsConfiguredFlags()
    {
        var controller = new FeatureFlagsController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCourtSync = true,
                ExternalCalendarSync = true,
                CourtEvents = false,
                DocumentTemplateLibrary = true,
            },
        }));

        var result = controller.Get();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AppFeatureFlagsDto>(ok.Value);

        Assert.True(dto.Legal.ExternalCourtSync);
        Assert.True(dto.Legal.ExternalCalendarSync);
        Assert.False(dto.Legal.CourtEvents);
        Assert.True(dto.Legal.DocumentTemplateLibrary);
    }

    [Fact]
    public void GetLegalIntegrationDiagnostics_ReturnsSafeDisabledStateWithoutSecrets()
    {
        var controller = new FeatureFlagsController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCourtSync = false,
                ExternalCalendarSync = false,
            },
        }));

        var result = controller.GetLegalIntegrationDiagnostics();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<LegalIntegrationDiagnosticsDto>(ok.Value);

        Assert.False(dto.HasEnabledExternalLegalFlags);
        Assert.Equal(2, dto.Items.Count);
        Assert.All(dto.Items, item =>
        {
            Assert.False(item.Enabled);
            Assert.True(item.SafeToRun);
            Assert.Equal("disabled", item.Status);
            Assert.DoesNotContain("token", item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", item.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void LegalIntegrationDiagnostics_FlagsEnabledRequireApproval()
    {
        var dto = LegalIntegrationDiagnosticsService.BuildDiagnostics(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCourtSync = true,
                ExternalCalendarSync = false,
            },
        });

        Assert.True(dto.HasEnabledExternalLegalFlags);
        var courtSync = Assert.Single(dto.Items, item => item.Key == "externalCourtSync");
        Assert.True(courtSync.Enabled);
        Assert.False(courtSync.SafeToRun);
        Assert.Equal("requiresApproval", courtSync.Status);
    }

    [Fact]
    public void CalendarSyncProviders_KeepIcsAvailableWhenExternalCalendarsAreDisabled()
    {
        var providers = CalendarSyncProviderRegistry.BuildProviders(externalCalendarSyncEnabled: false);

        var ics = Assert.Single(providers, item => item.Key == "ics-export");
        Assert.True(ics.IsAvailable);
        Assert.True(ics.SupportsManualExport);
        Assert.False(ics.SupportsTwoWaySync);
        Assert.Equal("active", ics.Status);

        var google = Assert.Single(providers, item => item.Key == "google-calendar");
        Assert.False(google.IsAvailable);
        Assert.False(google.FeatureFlagEnabled);
        Assert.True(google.RequiresOAuth);
        Assert.Equal("disabled", google.Status);

        var yandex = Assert.Single(providers, item => item.Key == "yandex-calendar");
        Assert.False(yandex.IsAvailable);
        Assert.False(yandex.FeatureFlagEnabled);
        Assert.True(yandex.RequiresOAuth);
        Assert.Equal("disabled", yandex.Status);
    }

    [Fact]
    public async Task CalendarSyncController_ReturnsProviderDiagnosticsFromFeatureFlag()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCalendarSync = true,
            },
        }), scope.Db);

        var result = await controller.GetProviders(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providers = Assert.IsAssignableFrom<IReadOnlyList<CalendarSyncProviderDto>>(ok.Value);

        Assert.Equal(3, providers.Count);
        Assert.Contains(providers, item => item.Key == "google-calendar" && item.FeatureFlagEnabled && item.Status == "requires-setup");
        Assert.Contains(providers, item => item.Key == "yandex-calendar" && item.FeatureFlagEnabled && item.Status == "requires-setup");
    }

    [Fact]
    public void CalendarSyncReadiness_KeepsExternalCalendarsBlockedUntilRequiredStepsAreReady()
    {
        var readiness = CalendarSyncProviderRegistry.BuildReadiness(externalCalendarSyncEnabled: true);

        Assert.True(readiness.ExternalCalendarSyncEnabled);
        Assert.True(readiness.GoogleCalendarSyncEnabled);
        Assert.True(readiness.YandexCalendarSyncEnabled);
        Assert.False(readiness.ReadyToConnect);
        Assert.Contains(readiness.Items, item => item.Key == "ics-fallback" && item.IsReady && !item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "google-admin-toggle" && item.IsReady && item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "yandex-admin-toggle" && item.IsReady && item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "google-oauth" && !item.IsReady && item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "conflict-policy" && !item.IsReady && item.IsRequired);
        Assert.All(readiness.Items, item =>
        {
            Assert.DoesNotContain("secret", item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", item.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CalendarSyncController_ReturnsReadinessFromFeatureFlag()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCalendarSync = false,
            },
        }), scope.Db);

        var result = await controller.GetReadiness(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var readiness = Assert.IsType<CalendarSyncReadinessDto>(ok.Value);

        Assert.False(readiness.ExternalCalendarSyncEnabled);
        Assert.False(readiness.GoogleCalendarSyncEnabled);
        Assert.False(readiness.YandexCalendarSyncEnabled);
        Assert.False(readiness.ReadyToConnect);
        Assert.Contains(readiness.Items, item => item.Key == "google-admin-toggle" && item.Status == "disabled" && !item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "yandex-admin-toggle" && item.Status == "disabled" && !item.IsRequired);
    }

    [Fact]
    public async Task CalendarSyncController_SavesSettingsAndUsesThemInReadiness()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCalendarSync = true,
            },
        }), scope.Db);

        var update = await controller.UpdateSettings(new UpdateCalendarSyncSettingsRequest
        {
            ExternalCalendarSyncEnabled = true,
            SyncMode = "hybrid",
            ConflictResolutionMode = "manualConfirmation",
            UserControlMode = "adminManaged",
            DefaultSyncFromDate = new DateTime(2026, 1, 1),
            InactiveUserAction = "disableSyncOnly",
            ManualFullSyncCooldownMinutes = 30,
            RequireManualConfirmation = false,
            DryRunConfirmed = true,
            Notes = "Проверено на копии базы.",
        }, CancellationToken.None);
        var updated = Assert.IsType<CalendarSyncSettingsDto>(Assert.IsType<OkObjectResult>(update.Result).Value);

        Assert.True(updated.ExternalCalendarSyncEnabled);
        Assert.True(updated.GoogleCalendarSyncEnabled);
        Assert.True(updated.YandexCalendarSyncEnabled);
        Assert.Equal("hybrid", updated.SyncMode);
        Assert.Equal("manualConfirmation", updated.ConflictResolutionMode);
        Assert.Equal("adminManaged", updated.UserControlMode);
        Assert.Equal(new DateTime(2026, 1, 1), updated.DefaultSyncFromDate);
        Assert.Equal("disableSyncOnly", updated.InactiveUserAction);
        Assert.Equal(30, updated.ManualFullSyncCooldownMinutes);
        Assert.True(updated.RequireManualConfirmation);
        Assert.True(updated.DryRunConfirmed);

        var auditEvent = Assert.Single(scope.Db.ObjectAuditEvents);
        Assert.Equal("calendar-sync", auditEvent.ObjectType);
        Assert.Equal("Updated", auditEvent.Action);
        Assert.Contains("гибридная модель", auditEvent.Description);
        Assert.Contains("SyncMode", auditEvent.MetadataJson);
        Assert.Contains("externalCalendarSyncEnabled", auditEvent.MetadataJson);
        Assert.Contains("googleCalendarSyncEnabled", auditEvent.MetadataJson);
        Assert.Contains("yandexCalendarSyncEnabled", auditEvent.MetadataJson);
        Assert.Contains("DryRunConfirmed", auditEvent.MetadataJson);
        Assert.Contains("ManualFullSyncCooldownMinutes", auditEvent.MetadataJson);

        var readinessResult = await controller.GetReadiness(CancellationToken.None);
        var readiness = Assert.IsType<CalendarSyncReadinessDto>(Assert.IsType<OkObjectResult>(readinessResult.Result).Value);

        Assert.False(readiness.ReadyToConnect);
        Assert.True(readiness.ExternalCalendarSyncEnabled);
        Assert.True(readiness.GoogleCalendarSyncEnabled);
        Assert.True(readiness.YandexCalendarSyncEnabled);
        Assert.Contains(readiness.Items, item => item.Key == "conflict-policy" && item.IsReady && item.Status == "ready");
        Assert.Contains(readiness.Items, item => item.Key == "dry-run" && item.IsReady && item.Status == "ready");
        Assert.Contains(readiness.Items, item => item.Key == "google-oauth" && !item.IsReady);
    }

    [Fact]
    public async Task CalendarSyncController_UsesAdminToggleWhenFeatureFlagIsDisabled()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCalendarSync = false,
            },
        }), scope.Db);

        await controller.UpdateSettings(new UpdateCalendarSyncSettingsRequest
        {
            ExternalCalendarSyncEnabled = true,
            SyncMode = "crmMaster",
            ConflictResolutionMode = "crmWins",
            RequireManualConfirmation = false,
            DryRunConfirmed = true,
        }, CancellationToken.None);

        var providersResult = await controller.GetProviders(CancellationToken.None);
        var providers = Assert.IsAssignableFrom<IReadOnlyList<CalendarSyncProviderDto>>(
            Assert.IsType<OkObjectResult>(providersResult.Result).Value);

        Assert.Contains(providers, item => item.Key == "google-calendar" && item.FeatureFlagEnabled && item.Status == "requires-setup");
        Assert.Contains(providers, item => item.Key == "yandex-calendar" && item.FeatureFlagEnabled && item.Status == "requires-setup");

        var readinessResult = await controller.GetReadiness(CancellationToken.None);
        var readiness = Assert.IsType<CalendarSyncReadinessDto>(Assert.IsType<OkObjectResult>(readinessResult.Result).Value);

        Assert.True(readiness.ExternalCalendarSyncEnabled);
        Assert.True(readiness.GoogleCalendarSyncEnabled);
        Assert.True(readiness.YandexCalendarSyncEnabled);
        Assert.Contains(readiness.Items, item => item.Key == "google-admin-toggle" && item.IsReady && item.Status == "ready");
        Assert.Contains(readiness.Items, item => item.Key == "yandex-admin-toggle" && item.IsReady && item.Status == "ready");
    }

    [Fact]
    public async Task CalendarSyncController_AllowsEnablingGoogleAndYandexSeparately()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions
        {
            Legal = new LegalFeatureFlagsOptions
            {
                ExternalCalendarSync = false,
            },
        }), scope.Db);

        var update = await controller.UpdateSettings(new UpdateCalendarSyncSettingsRequest
        {
            GoogleCalendarSyncEnabled = true,
            YandexCalendarSyncEnabled = false,
            SyncMode = "crmMaster",
            ConflictResolutionMode = "crmWins",
            RequireManualConfirmation = false,
            DryRunConfirmed = true,
        }, CancellationToken.None);
        var updated = Assert.IsType<CalendarSyncSettingsDto>(Assert.IsType<OkObjectResult>(update.Result).Value);

        Assert.True(updated.ExternalCalendarSyncEnabled);
        Assert.True(updated.GoogleCalendarSyncEnabled);
        Assert.False(updated.YandexCalendarSyncEnabled);

        var providersResult = await controller.GetProviders(CancellationToken.None);
        var providers = Assert.IsAssignableFrom<IReadOnlyList<CalendarSyncProviderDto>>(
            Assert.IsType<OkObjectResult>(providersResult.Result).Value);

        Assert.Contains(providers, item => item.Key == "google-calendar" && item.FeatureFlagEnabled && item.Status == "requires-setup");
        Assert.Contains(providers, item => item.Key == "yandex-calendar" && !item.FeatureFlagEnabled && item.Status == "disabled");

        var readinessResult = await controller.GetReadiness(CancellationToken.None);
        var readiness = Assert.IsType<CalendarSyncReadinessDto>(Assert.IsType<OkObjectResult>(readinessResult.Result).Value);

        Assert.True(readiness.ExternalCalendarSyncEnabled);
        Assert.True(readiness.GoogleCalendarSyncEnabled);
        Assert.False(readiness.YandexCalendarSyncEnabled);
        Assert.Contains(readiness.Items, item => item.Key == "google-oauth" && item.IsRequired && !item.IsReady);
        Assert.Contains(readiness.Items, item => item.Key == "yandex-oauth" && !item.IsRequired && item.Status == "disabled");
    }

    [Fact]
    public async Task CalendarSyncController_NormalizesInvalidSettings()
    {
        await using var scope = await CreateDbScopeAsync();
        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()), scope.Db);

        var result = await controller.UpdateSettings(new UpdateCalendarSyncSettingsRequest
        {
            SyncMode = "unexpected",
            ConflictResolutionMode = "unsafe",
            UserControlMode = "unsafe",
            ManualFullSyncCooldownMinutes = 1,
            DefaultSyncFromDate = new DateTime(2026, 1, 3, 14, 10, 0),
            InactiveUserAction = "unsafe",
            RequireManualConfirmation = false,
            DryRunConfirmed = false,
            Notes = new string('x', 600),
        }, CancellationToken.None);
        var settings = Assert.IsType<CalendarSyncSettingsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("notConfigured", settings.SyncMode);
        Assert.Equal("manualConfirmation", settings.ConflictResolutionMode);
        Assert.Equal("userManaged", settings.UserControlMode);
        Assert.Equal(CalendarSyncProviderRegistry.MinManualFullSyncCooldownMinutes, settings.ManualFullSyncCooldownMinutes);
        Assert.Equal(new DateTime(2026, 1, 3), settings.DefaultSyncFromDate);
        Assert.Equal("cleanupExternalData", settings.InactiveUserAction);
        Assert.True(settings.RequireManualConfirmation);
        Assert.Equal(500, settings.Notes.Length);
    }

    [Fact]
    public async Task CalendarSyncController_AdminEndpoints_ReturnForbidForNonAdminUser()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateCalendarSyncUserAsync(scope.Db, "calendar-sync-user", isAdmin: false);
        var controller = CreateCalendarSyncControllerForUser(scope.Db, user.Id);

        var updateResult = await controller.UpdateSettings(
            new UpdateCalendarSyncSettingsRequest
            {
                ExternalCalendarSyncEnabled = true,
            },
            CancellationToken.None);
        var logsResult = await controller.GetLogs(null, null, null, null, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResult.Result);
        Assert.IsType<ForbidResult>(logsResult.Result);
        Assert.False(await scope.Db.SystemSettings.AnyAsync(item => item.Key == "calendarSync.settings", CancellationToken.None));
    }

    [Fact]
    public async Task CalendarSyncController_GetLogs_ReturnsUnifiedProviderLog()
    {
        await using var scope = await CreateDbScopeAsync();
        var role = new Role { Name = "user", Description = "User" };
        var user = new User
        {
            Email = "calendar-log@example.com",
            FullName = "Пользователь календаря",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        var googleConnection = new GoogleCalendarConnection
        {
            User = user,
            GoogleEmail = "calendar-log@gmail.com",
            DisplayName = "Google",
        };
        var yandexConnection = new YandexCalendarConnection
        {
            User = user,
            YandexEmail = "calendar-log@yandex.ru",
            DisplayName = "Yandex",
        };
        scope.Db.GoogleCalendarConnections.Add(googleConnection);
        scope.Db.YandexCalendarConnections.Add(yandexConnection);
        await scope.Db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ObjectName = "Дело клиента",
            Title = "Подготовить документы",
            AssigneeName = user.FullName,
            DueAt = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
        };
        var calendar = new GoogleCalendarSyncCalendar
        {
            ConnectionId = googleConnection.Id,
            OwnerUserId = user.Id,
            GoogleCalendarId = "google-calendar-log",
            Name = "PayPlanner - Пользователь календаря",
        };
        scope.Db.TaskJournalItems.Add(task);
        scope.Db.GoogleCalendarSyncCalendars.Add(calendar);
        await scope.Db.SaveChangesAsync();

        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
        {
            ConnectionId = googleConnection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = task.Id,
            Direction = "crm-to-google",
            Operation = "create-event",
            Status = "success",
            DetailsJson = "{\"detail\":\"dry-run verified\"}",
            Message = "Событие создано.",
            CreatedAt = new DateTime(2026, 6, 2, 7, 0, 0, DateTimeKind.Utc),
        });
        scope.Db.YandexCalendarSyncLogs.Add(new YandexCalendarSyncLog
        {
            ConnectionId = yandexConnection.Id,
            Direction = "crm-to-yandex",
            Operation = "update-event",
            Status = "error",
            Message = "Событие не обновлено.",
            CreatedAt = new DateTime(2026, 6, 2, 7, 5, 0, DateTimeKind.Utc),
        });
        await scope.Db.SaveChangesAsync();

        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()), scope.Db);

        var result = await controller.GetLogs(null, null, null, null, pageSize: 10, ct: CancellationToken.None);
        var page = Assert.IsType<CalendarSyncLogPageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, page.Total);
        Assert.Equal("yandex-calendar", page.Items[0].Provider);
        Assert.Equal("google-calendar", page.Items[1].Provider);
        Assert.Equal(user.Id, page.Items[0].UserId);
        Assert.Equal("Пользователь календаря", page.Items[0].UserName);

        var pagedResult = await controller.GetLogs(null, null, null, null, page: 2, pageSize: 1, ct: CancellationToken.None);
        var paged = Assert.IsType<CalendarSyncLogPageDto>(Assert.IsType<OkObjectResult>(pagedResult.Result).Value);
        Assert.Equal(2, paged.Total);
        Assert.Equal(2, paged.Page);
        Assert.Equal("google-calendar", Assert.Single(paged.Items).Provider);

        var filteredResult = await controller.GetLogs("google-calendar", null, null, null, pageSize: 10, ct: CancellationToken.None);
        var filteredPage = Assert.IsType<CalendarSyncLogPageDto>(Assert.IsType<OkObjectResult>(filteredResult.Result).Value);

        var item = Assert.Single(filteredPage.Items);
        Assert.Equal("Google Calendar", item.ProviderLabel);
        Assert.Equal("create-event", item.Operation);
        Assert.Equal("Создание события", item.OperationLabel);
        Assert.Equal("CRM → Google Calendar", item.DirectionLabel);
        Assert.Equal("Успешно", item.StatusLabel);
        Assert.Equal(task.Id, item.TaskJournalItemId);
        Assert.Equal(task.Title, item.TaskTitle);
        Assert.Equal(task.ObjectName, item.TaskObjectName);
        Assert.Equal(task.AssigneeName, item.TaskAssigneeName);
        Assert.Equal(calendar.Name, item.CalendarName);
        Assert.Equal(user.FullName, item.CalendarOwnerName);
        Assert.Equal(user.Email, item.CalendarOwnerEmail);
        Assert.Equal("task", item.TargetType);
        Assert.Equal($"Задача: {task.Title}", item.TargetLabel);
        Assert.Equal("dry-run verified", item.Details);

        var dateFilteredResult = await controller.GetLogs(
            null,
            null,
            null,
            user.Id,
            createdFrom: new DateTime(2026, 6, 2, 7, 1, 0, DateTimeKind.Utc),
            createdTo: new DateTime(2026, 6, 2, 7, 10, 0, DateTimeKind.Utc),
            search: "Яндекс",
            pageSize: 10,
            ct: CancellationToken.None);
        var dateFilteredPage = Assert.IsType<CalendarSyncLogPageDto>(Assert.IsType<OkObjectResult>(dateFilteredResult.Result).Value);
        var dateFilteredItem = Assert.Single(dateFilteredPage.Items);
        Assert.Equal("yandex-calendar", dateFilteredItem.Provider);
    }

    [Fact]
    public async Task CalendarSyncController_GetLogs_InfersCalendarOwnerFromDeletedCalendarName()
    {
        await using var scope = await CreateDbScopeAsync();
        scope.Db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog
        {
            Direction = "system",
            Operation = "delete-calendar",
            Status = "success",
            Message = "Календарь «PayPlanner - Иван Иванов» удален из Google Calendar.",
            CreatedAt = new DateTime(2026, 6, 2, 7, 0, 0, DateTimeKind.Utc),
        });
        await scope.Db.SaveChangesAsync();

        var controller = new CalendarSyncController(new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()), scope.Db);

        var result = await controller.GetLogs("google-calendar", null, null, null, pageSize: 10, ct: CancellationToken.None);
        var page = Assert.IsType<CalendarSyncLogPageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var item = Assert.Single(page.Items);

        Assert.Equal("PayPlanner - Иван Иванов", item.CalendarName);
        Assert.Equal("Иван Иванов", item.CalendarOwnerName);
        Assert.Equal("Иван Иванов", item.UserName);
        Assert.Equal("calendar", item.TargetType);
    }

    private static CalendarSyncController CreateCalendarSyncControllerForUser(PaymentContext db, int userId)
    {
        var controller = new CalendarSyncController(
            new TestOptionsSnapshot<AppFeatureFlagsOptions>(new AppFeatureFlagsOptions()),
            db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "Test user"),
                    ], "Test")),
                },
            },
        };

        return controller;
    }

    private static async Task<User> CreateCalendarSyncUserAsync(PaymentContext db, string name, bool isAdmin)
    {
        var role = new Role
        {
            Name = isAdmin ? "admin" : $"{name}-{Guid.NewGuid():N}",
            Description = name,
        };
        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class TestOptionsSnapshot<T> : IOptionsSnapshot<T>
        where T : class
    {
        public TestOptionsSnapshot(T value)
        {
            Value = value;
        }

        public T Value { get; }

        public T Get(string? name) => Value;
    }

    private static async Task<TestDbScope> CreateDbScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();

        return new TestDbScope(connection, db);
    }

    private sealed class TestDbScope(SqliteConnection connection, PaymentContext db) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
