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

        var result = controller.GetProviders();
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
        Assert.False(readiness.ReadyToConnect);
        Assert.Contains(readiness.Items, item => item.Key == "ics-fallback" && item.IsReady && !item.IsRequired);
        Assert.Contains(readiness.Items, item => item.Key == "feature-flag" && item.IsReady && item.IsRequired);
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
        Assert.False(readiness.ReadyToConnect);
        Assert.Contains(readiness.Items, item => item.Key == "feature-flag" && item.Status == "disabled");
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
            SyncMode = "hybrid",
            ConflictResolutionMode = "manualConfirmation",
            RequireManualConfirmation = false,
            DryRunConfirmed = true,
            Notes = "Проверено на копии базы.",
        }, CancellationToken.None);
        var updated = Assert.IsType<CalendarSyncSettingsDto>(Assert.IsType<OkObjectResult>(update.Result).Value);

        Assert.Equal("hybrid", updated.SyncMode);
        Assert.Equal("manualConfirmation", updated.ConflictResolutionMode);
        Assert.True(updated.RequireManualConfirmation);
        Assert.True(updated.DryRunConfirmed);

        var readinessResult = await controller.GetReadiness(CancellationToken.None);
        var readiness = Assert.IsType<CalendarSyncReadinessDto>(Assert.IsType<OkObjectResult>(readinessResult.Result).Value);

        Assert.False(readiness.ReadyToConnect);
        Assert.Contains(readiness.Items, item => item.Key == "conflict-policy" && item.IsReady && item.Status == "ready");
        Assert.Contains(readiness.Items, item => item.Key == "dry-run" && item.IsReady && item.Status == "ready");
        Assert.Contains(readiness.Items, item => item.Key == "google-oauth" && !item.IsReady);
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
            RequireManualConfirmation = false,
            DryRunConfirmed = false,
            Notes = new string('x', 600),
        }, CancellationToken.None);
        var settings = Assert.IsType<CalendarSyncSettingsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("notConfigured", settings.SyncMode);
        Assert.Equal("manualConfirmation", settings.ConflictResolutionMode);
        Assert.True(settings.RequireManualConfirmation);
        Assert.Equal(500, settings.Notes.Length);
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
