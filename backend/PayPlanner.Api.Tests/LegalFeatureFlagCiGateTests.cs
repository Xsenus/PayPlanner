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
        Assert.False(legal.GetProperty("OfflineCalendarReadOnly").GetBoolean());
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
                "dotnet restore \"${{ env.BE_SLN }}\" --force -p:PayPlannerReleaseAudit=true",
                "dotnet format",
                "npm ci",
                "npx playwright install --with-deps chromium chrome",
                "npm install --global @playwright/cli@0.1.22",
                "dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore",
                "npm run lint",
                "npx tsc -p tsconfig.app.json --noEmit",
                "npm run audit:dates:test",
                "npm run audit:encoding",
                "npm run audit:surface:test && npm run audit:surface",
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
        Assert.Contains("sudo rsync -a --delete \"$STAGE/fe/\" \"$FE_PUBLIC_DIR/\"", workflow, StringComparison.Ordinal);
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
    public void DeployWorkflow_PreservesHistoricalDataProtectionKeysBeforeChangingRuntimeOrRelease()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("bash scripts/preserve-data-protection-keys.test.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("cp scripts/preserve-data-protection-keys.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$previous_keys_path\" \"$previous_keys_directory\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$service_home/.aspnet/DataProtection-Keys\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$BACKUP_DIR/data-protection-keys.previous\" existing", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("printf 'DataProtection__ApplicationName=", workflow, StringComparison.Ordinal);
        AssertInOrder(workflow,
        [
            "            preserve_data_protection_keys\n",
            "            configure_runtime_env\n",
            "sudo rsync -a --delete \"$STAGE/be/\"",
            "sudo systemctl restart payplanner.service",
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
                "sudo rsync -a --delete \"$STAGE/fe/\" \"$FE_APP_DIR/\"",
                "sudo rsync -a --delete \"$STAGE/fe/\" \"$FE_PUBLIC_DIR/\"",
                "sudo systemctl enable payplanner.service",
                "sudo systemctl restart payplanner.service",
                "curl -fsS \"$PRIMARY_HEALTH_URL\"",
            ]);
    }

    [Fact]
    public void DeployWorkflow_PreservesUploadedFilesOutsideReleaseDirectory()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));

        Assert.Contains("PERSISTENT_FILE_ROOT=\"/opt/payplanner/shared/files\"", workflow, StringComparison.Ordinal);
        Assert.Contains("FileStorage__RootPath=%s", workflow, StringComparison.Ordinal);
        Assert.Contains("migrate_legacy_file_storage()", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$BE_DIR/App_Data/\" \"$PERSISTENT_FILE_ROOT/\"", workflow, StringComparison.Ordinal);
        Assert.Contains("-path '*/backend.previous/App_Data'", workflow, StringComparison.Ordinal);
        Assert.Contains("rsync -a --ignore-existing", workflow, StringComparison.Ordinal);
        Assert.Contains("Persistent file storage restored:", workflow, StringComparison.Ordinal);
        AssertInOrder(
            workflow,
            [
                "Backup current release",
                "echo \"[6/14] Restore persistent file storage\"",
                "sudo rsync -a --delete \"$STAGE/be/\" \"$BE_DIR/\"",
                "sudo systemctl restart payplanner.service",
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
        var current = TestRepository.FindDirectory();

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
