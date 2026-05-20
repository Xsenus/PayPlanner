using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
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
