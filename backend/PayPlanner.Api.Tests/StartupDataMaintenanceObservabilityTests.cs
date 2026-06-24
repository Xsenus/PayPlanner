using Xunit;

namespace PayPlanner.Api.Tests;

public class StartupDataMaintenanceObservabilityTests
{
    private static readonly string[] RequiredStartupSteps =
    [
        "database migrations",
        "seed data",
        "app release sync",
        "payment remainder transfer backfill",
        "task contractor payment backfill",
        "legacy expense recipient backfill",
    ];

    [Fact]
    public void Program_LogsStartupDataMaintenanceSteps()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Program.cs"));

        Assert.Contains("RunStartupStepAsync", program, StringComparison.Ordinal);
        Assert.Contains("Startup data step {StepName} started.", program, StringComparison.Ordinal);
        Assert.Contains("Startup data step {StepName} completed in {ElapsedMs} ms.", program, StringComparison.Ordinal);
        Assert.Contains("Startup data step {StepName} failed after {ElapsedMs} ms.", program, StringComparison.Ordinal);

        foreach (var step in RequiredStartupSteps)
        {
            Assert.Contains(step, program, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && File.Exists(Path.Combine(current.FullName, "backend", "Program.cs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
