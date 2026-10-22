using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SyntheticWritableBackendTemplateTests : IClassFixture<SqliteMigratedTemplate>
{
    private readonly SqliteMigratedTemplate template;

    public SyntheticWritableBackendTemplateTests(SqliteMigratedTemplate template) => this.template = template;

    [Fact]
    public async Task ClonedRuntimeKeepsOwnDataAcrossRestartAndDoesNotLeakIntoNextRuntime()
    {
        const string marker = "test.synthetic.runtime.isolation";
        foreach (var mode in new[] { "legacy", "modern" })
        {
            await using var runtime = await SyntheticWritableBackendProcess.StartAsync(cardMode: mode, template: template);
            await using (var db = runtime.OpenDatabase())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal("false", await db.SystemSettings.Where(row => row.Key == "notifications.email.enabled").Select(row => row.Value).SingleAsync());
                Assert.Equal(mode, await db.SystemSettings.Where(row => row.Key == "tasks.taskCardWindowMode").Select(row => row.Value).SingleAsync());
                Assert.False(await db.SystemSettings.AnyAsync(row => row.Key == marker));
                db.SystemSettings.Add(new SystemSetting { Key = marker, Value = mode });
                await db.SaveChangesAsync();
            }
            await runtime.RestartAsync(TimeSpan.Zero);
            await using (var db = runtime.OpenDatabase())
                Assert.Equal(mode, await db.SystemSettings.Where(row => row.Key == marker).Select(row => row.Value).SingleAsync());
            runtime.AssertNoServerErrors();
        }
    }
}
