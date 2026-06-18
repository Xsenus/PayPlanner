using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LegalFeatureFlagCiGateTests
{
    [Fact]
    public void StrictLegalFlagReadinessScript_UsesPublicReadinessGate()
    {
        var root = FindRepositoryRoot();
        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
        var scripts = packageJson.RootElement.GetProperty("scripts");
        var command = scripts.GetProperty("test:legal-flags:strict").GetString();

        Assert.NotNull(command);
        Assert.Contains("scripts/run-legal-readiness-report.mjs", command, StringComparison.Ordinal);
        Assert.Contains("--public-only", command, StringComparison.Ordinal);
        Assert.Contains("--skip-e2e", command, StringComparison.Ordinal);
        Assert.Contains("--skip-load", command, StringComparison.Ordinal);
        Assert.Contains("--fail-on-enabled-legal-flags", command, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultConfiguration_KeepsLegalAndCalendarIntegrationFlagsDisabled()
    {
        var root = FindRepositoryRoot();
        using var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "backend", "appsettings.json")));
        var legal = appsettings.RootElement
            .GetProperty("FeatureFlags")
            .GetProperty("Legal");

        Assert.False(legal.GetProperty("ExternalCourtSync").GetBoolean());
        Assert.False(legal.GetProperty("ExternalCalendarSync").GetBoolean());
        Assert.False(legal.GetProperty("CourtEvents").GetBoolean());
        Assert.False(legal.GetProperty("DocumentTemplateLibrary").GetBoolean());
    }

    [Fact]
    public void DeployWorkflow_RunsStrictLegalFlagGateBeforePublish()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));
        var gateIndex = workflow.IndexOf("npm run test:legal-flags:strict", StringComparison.Ordinal);
        var publishIndex = workflow.IndexOf("dotnet publish", StringComparison.Ordinal);

        Assert.True(gateIndex >= 0, "deploy.yml must run npm run test:legal-flags:strict.");
        Assert.True(publishIndex >= 0, "deploy.yml must publish backend after verification gates.");
        Assert.True(gateIndex < publishIndex, "legal feature flag readiness gate must run before artifacts are published.");
        Assert.Contains("PAYPLANNER_READINESS_API_URL", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployWorkflow_ConfiguresWebPushRuntimeEnvironmentSafely()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        Assert.Contains("PAYPLANNER_WEBPUSH_ENABLED", workflow, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_PUBLIC_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_PRIVATE_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_SUBJECT", workflow, StringComparison.Ordinal);
        Assert.Contains("WebPush__Enabled", workflow, StringComparison.Ordinal);
        Assert.Contains("WebPush__PublicKey", workflow, StringComparison.Ordinal);
        Assert.Contains("WebPush__PrivateKey", workflow, StringComparison.Ordinal);
        Assert.Contains("WebPush__Subject", workflow, StringComparison.Ordinal);
        Assert.Contains("/opt/payplanner/shared/payplanner.env", workflow, StringComparison.Ordinal);
        Assert.Contains("EnvironmentFile=-%s", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-p Environment", workflow, StringComparison.Ordinal);
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
