using System.Diagnostics;
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
    public async Task DeployWorkflow_RunsStrictLegalFlagGateBeforeVpsDeploy()
    {
        var result = await ValidateDeployWorkflowAsync();
        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.Contains("frontend", result.GetProperty("deployNeeds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void DeployWorkflow_ConfiguresWebPushRuntimeEnvironmentSafely()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root);

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
    public async Task DeployWorkflow_KeepsVerificationGatesBeforeArtifactsAndVpsDeploy()
    {
        var result = await ValidateDeployWorkflowAsync();
        Assert.Equal(new[] { "backend", "backend-format", "frontend" },
            result.GetProperty("deployNeeds").EnumerateArray().Select(item => item.GetString()).OrderBy(item => item));
        Assert.Equal(2, result.GetProperty("artifacts").GetArrayLength());
    }

    [Fact]
    public void DeployWorkflow_BuildsFrontendWithApiUrlAndPublishesServiceWorkerArtifact()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root);

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
                "node scripts/windows-vps-deploy.mjs",
                "Check production deploy readiness",
                "scripts/run-production-deploy-readiness.mjs",
            ]);
    }

    [Fact]
    public void DeployWorkflow_PreservesHistoricalDataProtectionKeysBeforeChangingRuntimeOrRelease()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("bash scripts/preserve-data-protection-keys.test.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("cp scripts/preserve-data-protection-keys.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$previous_keys_path\" \"$previous_keys_directory\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$service_home/.aspnet/DataProtection-Keys\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$BACKUP_DIR/data-protection-keys.previous\" existing", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("printf 'DataProtection__ApplicationName=", workflow, StringComparison.Ordinal);
        AssertInOrder(workflow,
        [
            "\npreserve_data_protection_keys\n",
            "\nconfigure_runtime_env\n",
            "sudo rsync -a --delete \"$STAGE/be/\"",
            "sudo systemctl restart payplanner.service",
        ]);
    }

    [Fact]
    public void DeployWorkflow_RestartsBackendOnlyOnceAfterFilesAreSynced()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root);

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
        var workflow = ReadWorkflowAndDeployment(root);

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
    public void DeployWorkflow_RunsLinuxFileSystemGatesBeforeTransferringRelease()
    {
        var root = FindRepositoryRoot();
        var bridge = File.ReadAllText(Path.Combine(root, "scripts", "windows-vps-deploy.mjs"));

        Assert.Contains("test \"$(uname -s)\" = Linux", bridge, StringComparison.Ordinal);
        Assert.Contains("export TMPDIR=\"$PWD/fixtures\"", bridge, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c -", bridge, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=yes", bridge, StringComparison.Ordinal);
        AssertInOrder(bridge,
        [
            "const knownHosts = trustedHostLine(",
            "directory = workspace(config, run)",
            "writeFileSync(join(directory, 'identity')",
            "bash prune-deploy-backups.test.sh",
            "bash preserve-data-protection-keys.test.sh",
            "{ input: preflight, timeout: 300000 }",
            "execute('scp', [...scpArgs, config.archive",
            "{ input: payload, timeout: 1200000 }",
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

    private static async Task<JsonElement> ValidateDeployWorkflowAsync()
    {
        var root = FindRepositoryRoot();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "scripts", "validate-deploy-workflow.mjs"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Workflow validator did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode == 0, await stderr);
            using var document = JsonDocument.Parse(await stdout);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static string ReadWorkflowAndDeployment(string root)
    {
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy.yml"));
        var bridge = File.ReadAllText(Path.Combine(root, "scripts", "windows-vps-deploy.mjs"));
        Assert.Contains("node scripts/windows-vps-deploy.mjs", workflow, StringComparison.Ordinal);
        Assert.Contains("content.get(join(config.root, 'scripts/deploy-vps.sh')).toString('utf8')", bridge, StringComparison.Ordinal);
        return workflow + "\n" + File.ReadAllText(Path.Combine(root, "scripts", "deploy-vps.sh"));
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
            Assert.True(index >= 0, $"Deployment sources must contain '{marker}'.");
            Assert.True(index > previousIndex, $"Deployment sources must keep '{marker}' after previous verification/deploy step.");
            previousIndex = index;
        }
    }
}
