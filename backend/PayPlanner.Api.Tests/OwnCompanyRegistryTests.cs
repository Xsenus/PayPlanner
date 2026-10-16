using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnCompanyRegistryTests
{
    [Theory]
    [InlineData(null, null, "")]
    [InlineData("[2,1,2,0,-3]", "9", "1,2")]
    [InlineData("[]", "9", "9")]
    [InlineData("invalid", "9", "9")]
    [InlineData("[\"2\"]", "9", "9")]
    [InlineData("null", "9", "9")]
    [InlineData("[-1,0]", "bad", "")]
    [InlineData("  ", "3", "3")]
    [InlineData("[2]", "3", "2")]
    [InlineData("{}", "4", "4")]
    public async Task ReadsExistingRegistryAndPreservesLegacyFallback(string? main, string? legacy, string expected)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        if (main is not null) db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = main });
        if (legacy is not null) db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = legacy });
        await db.SaveChangesAsync();

        var ids = await OwnCompanyRegistry.LoadIdsAsync(db);

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse), ids.OrderBy(id => id));
    }
}
