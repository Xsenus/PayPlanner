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
        Assert.Contains("PAYPLANNER_WEBPUSH_ENABLED: ${{ vars.PAYPLANNER_WEBPUSH_ENABLED || 'false' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("local webpush_enabled=\"${PAYPLANNER_WEBPUSH_ENABLED:-false}\"", workflow, StringComparison.Ordinal);
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

    [Fact]
    public void DeployWorkflow_KeepsVerificationGatesBeforeArtifactsAndVpsDeploy()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        AssertInOrder(
            workflow,
            [
                "dotnet restore",
                "dotnet format",
                "dotnet list",
                "dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore",
                "npm ci",
                "npm run lint",
                "npx tsc -p tsconfig.app.json --noEmit",
                "npm run audit:dates:test",
                "npm audit --audit-level=moderate",
                "npm run test:ci",
                "npm run test:integration",
                "npm run test:legal-flags:strict",
                "dotnet publish",
                "npm run build",
                "tar -czf",
                "appleboy/scp-action",
                "appleboy/ssh-action",
            ]);
    }

    [Fact]
    public void DeployWorkflow_BuildsFrontendWithApiUrlAndPublishesServiceWorkerArtifact()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        Assert.Contains("VITE_API_URL: ${{ env.FE_API_URL }}", workflow, StringComparison.Ordinal);
        Assert.Contains("cp -r dist/* \"${{ env.OUT_DIR }}/fe/\"", workflow, StringComparison.Ordinal);
        Assert.Contains("sudo rsync -a \"$STAGE/fe/\" \"$FE_PUBLIC_DIR/\"", workflow, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_API_HEALTH_URL", workflow, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_FRONTEND_URL", workflow, StringComparison.Ordinal);
        Assert.Contains("sudo tee \"$APP_DIR/.deploy-revision\"", workflow, StringComparison.Ordinal);
        Assert.Contains("curl -fsS \"$PRIMARY_HEALTH_URL\" | grep -Eq", workflow, StringComparison.Ordinal);
        Assert.Contains("curl -fsS \"$PUBLIC_API_HEALTH_URL\" | grep -Eq", workflow, StringComparison.Ordinal);
        Assert.Contains("curl -fsS -o /dev/null \"$PUBLIC_FRONTEND_URL\"", workflow, StringComparison.Ordinal);

        var pushWorker = Path.Combine(root, "public", "payplanner-push-worker.js");
        Assert.True(File.Exists(pushWorker), "public/payplanner-push-worker.js must exist so Vite copies it to dist during npm run build.");
    }

    [Fact]
    public void DeployWorkflow_RunsNotificationReadinessAfterVpsDeploy()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        Assert.Contains("PUBLIC_API_URL: https://api.elitpravo.com", workflow, StringComparison.Ordinal);
        Assert.Contains("Check production deploy readiness", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/run-production-deploy-readiness.mjs", workflow, StringComparison.Ordinal);
        Assert.Contains("--api-url=\"${{ env.PUBLIC_API_URL }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--frontend-url=\"${{ env.PUBLIC_FRONTEND_URL }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--token=\"\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--require-websocket=true", workflow, StringComparison.Ordinal);
        Assert.Contains("--allow-websocket-auth-challenge=true", workflow, StringComparison.Ordinal);
        AssertInOrder(
            workflow,
            [
                "appleboy/ssh-action",
                "Check production deploy readiness",
                "scripts/run-production-deploy-readiness.mjs",
            ]);
    }

    [Fact]
    public void DeployWorkflow_RestartsBackendOnlyOnceAfterFilesAreSynced()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        Assert.DoesNotContain("sudo systemctl stop payplanner.service", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo systemctl enable --now payplanner.service", workflow, StringComparison.Ordinal);
        AssertInOrder(
            workflow,
            [
                "sudo rsync -a --delete \"$STAGE/be/\" \"$BE_DIR/\"",
                "sudo rsync -a \"$STAGE/fe/\" \"$FE_APP_DIR/\"",
                "sudo systemctl enable payplanner.service",
                "sudo systemctl restart payplanner.service",
                "curl -fsS \"$PRIMARY_HEALTH_URL\"",
            ]);
    }

    [Fact]
    public void BackendConfiguration_UsesStableEfQueryWarningsPolicy()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Program.cs"));

        Assert.Contains("UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)", program, StringComparison.Ordinal);
        Assert.Contains("warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning)", program, StringComparison.Ordinal);
        Assert.Contains("warnings.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning)", program, StringComparison.Ordinal);
        Assert.Contains("warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning)", program, StringComparison.Ordinal);
        Assert.Contains("warnings.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning)", program, StringComparison.Ordinal);
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

    private static void AssertInOrder(string text, IReadOnlyList<string> markers)
    {
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"deploy.yml must contain '{marker}'.");
            Assert.True(index > previousIndex, $"deploy.yml must keep '{marker}' after previous verification/deploy step.");
            previousIndex = index;
        }
    }
}
