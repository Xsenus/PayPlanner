using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

internal static class MainCompanySettingsVersionTestAccess
{
    // Fixture/form-load baseline through the public reader, never a refreshed
    // version injected into a request that is intentionally stale.
    internal static async Task<string> ReadAsync(LegalEntitiesController controller)
        => Assert.IsType<MainCompanySettingsDto>(Assert.IsType<OkObjectResult>((await controller.GetMainCompanySettings(default)).Result).Value).SettingsVersion!;
}
