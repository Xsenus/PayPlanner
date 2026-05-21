using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
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
}
