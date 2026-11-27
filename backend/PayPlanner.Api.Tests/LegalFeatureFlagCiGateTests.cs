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
        Assert.Contains("PAYPLANNER_WEBPUSH_PUBLIC_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_PRIVATE_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_SUBJECT", workflow, StringComparison.Ordinal);
        // The reviewed installer preserves effective configuration instead of regenerating production secrets.
        Assert.Contains("ENV_FILE=/opt/payplanner/shared/payplanner.env", workflow, StringComparison.Ordinal);
        Assert.Contains("systemctl show \"$SERVICE\" -p FragmentPath -p DropInPaths -p EnvironmentFiles", workflow, StringComparison.Ordinal);
        Assert.Contains("python3 - \"$ENV_FILE\" \"$KEYS\" \"$FILES\" \"$BE/appsettings.json\" \"$BE/appsettings.Production.json\"", workflow, StringComparison.Ordinal);
        Assert.Contains("for name in appsettings.json appsettings.Production.json; do", workflow, StringComparison.Ordinal);
        Assert.Contains("cp -a \"$BACKUP/backend.previous/$name\" \"$BE/$name\"", workflow, StringComparison.Ordinal);
        Assert.Contains("cmp -s \"$BACKUP/preserved-before.private.json\" \"$BACKUP/preserved-started.private.json\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-p Environment ", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("printf 'WebPush__", workflow, StringComparison.Ordinal);
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
        Assert.Contains("rsync -a --checksum --delete \"$STAGE/fe/\" \"$PUBLIC/\"", workflow, StringComparison.Ordinal);
        Assert.Contains("printf '%s\\n' \"$REV\" > \"$APP/.deploy-revision\"", workflow, StringComparison.Ordinal);
        Assert.Contains("'http://127.0.0.1:5080/api/health','https://api.elitpravo.com/api/health'", workflow, StringComparison.Ordinal);
        Assert.Contains("body,_=get(url);assert json.loads(body)['status']=='ok'", workflow, StringComparison.Ordinal);
        Assert.Contains("body,headers=get('https://crm.elitpravo.com/')", workflow, StringComparison.Ordinal);
        Assert.Contains("hashlib.sha256(body).hexdigest()==expected['index.html']", workflow, StringComparison.Ordinal);
        Assert.Contains("headers.get('Strict-Transport-Security')", workflow, StringComparison.Ordinal);
        Assert.Contains("get('https://crm.elitpravo.com/deploy-revision.txt?deployment='+rev);assert body==(rev+'\\n').encode()", workflow, StringComparison.Ordinal);
        Assert.Contains("['payplanner-app-cache.js','payplanner-push-worker.js','payplanner-worker.js']", workflow, StringComparison.Ordinal);
        Assert.Contains("assert hashlib.sha256(body).hexdigest()==digest", workflow, StringComparison.Ordinal);

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

        var bridge = File.ReadAllText(Path.Combine(root, "scripts", "windows-vps-deploy.mjs"));
        Assert.Contains("bash preserve-data-protection-keys.test.sh", bridge, StringComparison.Ordinal);
        Assert.Contains("KEYS=/opt/payplanner/shared/data-protection-keys", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$ENV_FILE\" \"$KEYS\" \"$FILES\"", workflow, StringComparison.Ordinal);
        Assert.Contains("r={'mode':stat.S_IMODE(s.st_mode),'uid':s.st_uid,'gid':s.st_gid}", workflow, StringComparison.Ordinal);
        Assert.Contains("r['sha256']=hashlib.file_digest(f,'sha256').hexdigest()", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("printf 'DataProtection__ApplicationName=", workflow, StringComparison.Ordinal);
        AssertInOrder(workflow,
        [
            "  systemctl stop \"$SERVICE\"",
            "  preserved_tree \"$BACKUP/preserved-before.private.json\"",
            "tar --acls --xattrs --anchored --no-wildcards --exclude=opt/payplanner/shared/backups",
            "r['allListedFilesCopied'] is True and r['allCopiedHashesMatched'] is True",
            "rsync -a --checksum --delete \"$STAGE/be/\"",
            "preserved_tree \"$BACKUP/preserved-installed-stopped.private.json\"",
            "STARTUP_ATTEMPTED=true",
            "preserved_tree \"$BACKUP/preserved-started.private.json\"",
        ]);
    }

    [Fact]
    public void DeployWorkflow_RestartsBackendOnlyOnceAfterFilesAreSynced()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root).Replace("\r\n", "\n", StringComparison.Ordinal);

        AssertInOrder(workflow,
        [
            "  systemctl stop \"$SERVICE\"",
            "pg_dump --format=custom --no-owner --no-acl payplanner",
            "BACKUP_READY_SERVICE_STOPPED",
            "r['allListedFilesCopied'] is True and r['allCopiedHashesMatched'] is True",
            "rsync -a --checksum --delete \"$STAGE/be/\" \"$BE/\"",
        ]);
        var switchIndex = workflow.IndexOf("\nSWITCHED=true\n", StringComparison.Ordinal);
        Assert.True(switchIndex >= 0, "The reviewed live-switch boundary must exist.");
        var installation = workflow[switchIndex..];
        Assert.Equal(1, installation.Split("\nsystemctl start \"$SERVICE\"\n", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("systemctl restart", installation, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo systemctl enable --now payplanner.service", workflow, StringComparison.Ordinal);
        AssertInOrder(
            installation,
            [
                "rsync -a --checksum --delete \"$STAGE/be/\" \"$BE/\"",
                "rsync -a --checksum --delete \"$STAGE/fe/\" \"$FE/\"",
                "rsync -a --checksum --delete \"$STAGE/fe/\" \"$PUBLIC/\"",
                "db_proof \"$BACKUP/database-installed-stopped.json\"",
                "systemctl start \"$SERVICE\"",
                "curl -fsS --max-time 5 http://127.0.0.1:5080/api/health",
                "revision_is \"$REV\"",
                "\npublic_check\n",
            ]);
    }

    [Fact]
    public void DeployWorkflow_PreservesUploadedFilesOutsideReleaseDirectory()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadWorkflowAndDeployment(root);

        Assert.Contains("FILES=/opt/payplanner/shared/files", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$ENV_FILE\" \"$KEYS\" \"$FILES\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Backend-local App_Data needs explicit preservation review.", workflow, StringComparison.Ordinal);
        Assert.Contains("cp -a \"$BE\" \"$BACKUP/backend.previous\"", workflow, StringComparison.Ordinal);
        Assert.Contains("tar --acls --xattrs --anchored --no-wildcards --exclude=opt/payplanner/shared/backups", workflow, StringComparison.Ordinal);
        Assert.Contains("-C / opt/payplanner/shared", workflow, StringComparison.Ordinal);
        Assert.Contains("cmp -s \"$BACKUP/preserved-before.private.json\" \"$BACKUP/preserved-started.private.json\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--exclude=opt/payplanner/shared/files", workflow, StringComparison.Ordinal);
        AssertInOrder(
            workflow,
            [
                "  preserved_tree \"$BACKUP/preserved-before.private.json\"",
                "  cp -a \"$BE\" \"$BACKUP/backend.previous\"",
                "rsync -a --checksum --delete \"$STAGE/be/\" \"$BE/\"",
                "cmp -s \"$BACKUP/preserved-before.private.json\" \"$BACKUP/preserved-installed-stopped.private.json\"",
                "STARTUP_ATTEMPTED=true",
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
            "{ input: checks, timeout: 300000 }",
            "execute('scp', [...scpArgs, config.archive",
            "phase('backup')",
            "join(config.root, 'scripts/deploy-stopped-archive.py')",
            "assert.equal(verified.offsitePgRestoreListExitCode, 0)",
            "phase('install', { serverRelativePath: 'offsite-verified.json', sha256: hash(receiptRaw) })",
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
        Assert.Contains("uses: ./.github/workflows/release-checks.yml", workflow, StringComparison.Ordinal);
        var hostedChecks = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-checks.yml"));
        var bridge = File.ReadAllText(Path.Combine(root, "scripts", "windows-vps-deploy.mjs"));
        Assert.Contains("node scripts/windows-vps-deploy.mjs", workflow, StringComparison.Ordinal);
        Assert.Contains("const controlFiles = ['deploy-vps.sh',", bridge, StringComparison.Ordinal);
        Assert.Contains("writeFileSync(join(directory, name), content.get(join(config.root, 'scripts', name)), { flag: 'wx' })", bridge, StringComparison.Ordinal);
        Assert.Contains("...controlFiles.map(name => join(directory, name))", bridge, StringComparison.Ordinal);
        Assert.Contains("bash ${shellQuote(control + '/deploy-vps.sh')} --execute ${name} ${digest}", bridge, StringComparison.Ordinal);
        return workflow + "\n" + hostedChecks + "\n" + File.ReadAllText(Path.Combine(root, "scripts", "deploy-vps.sh"));
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
