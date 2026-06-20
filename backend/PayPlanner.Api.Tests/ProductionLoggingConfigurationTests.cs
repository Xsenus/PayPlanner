using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ProductionLoggingConfigurationTests
{
    [Fact]
    public void ProductionLogging_SuppressesRoutineEntityFrameworkCommandLogs()
    {
        var root = FindRepositoryRoot();
        using var appsettings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "backend", "appsettings.Production.json")));

        var logLevel = appsettings.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.Equal(
            "Warning",
            logLevel.GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json"))
                && Directory.Exists(Path.Combine(current.FullName, ".github"))
                && Directory.Exists(Path.Combine(current.FullName, "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
