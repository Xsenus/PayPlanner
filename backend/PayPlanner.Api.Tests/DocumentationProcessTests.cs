using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DocumentationProcessTests
{
    [Fact]
    public void Readme_DocumentsCurrentLocalSetupAndDocumentationEntrypoints()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("npm ci", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet restore backend\\PayPlanner.Api.sln", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project backend\\PayPlanner.Api.csproj", readme, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Default", readme, StringComparison.Ordinal);
        Assert.Contains("Jwt__Secret", readme, StringComparison.Ordinal);
        Assert.Contains("WebPush__PublicKey", readme, StringComparison.Ordinal);
        Assert.Contains("WebPush__PrivateKey", readme, StringComparison.Ordinal);
        Assert.Contains("WebPush__Subject", readme, StringComparison.Ordinal);
        Assert.Contains("docs/quality-gates.md", readme, StringComparison.Ordinal);
        Assert.Contains("docs/release-note-policy.md", readme, StringComparison.Ordinal);
        Assert.Contains("docs/deploy-runbook.md", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityGatesDocument_ListsReleaseGateCommands()
    {
        var root = FindRepositoryRoot();
        var qualityGates = File.ReadAllText(Path.Combine(root, "docs", "quality-gates.md"));

        Assert.Contains("npm ci", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run lint", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npx tsc -p tsconfig.app.json --noEmit", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run test:ci", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run test:integration", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run audit:dates:test", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run audit:encoding", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run audit:surface:test", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run audit:surface", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm run build", qualityGates, StringComparison.Ordinal);
        Assert.Contains("dotnet test backend\\PayPlanner.Api.sln --no-restore -c Debug", qualityGates, StringComparison.Ordinal);
        Assert.Contains("dotnet format backend\\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal", qualityGates, StringComparison.Ordinal);
        Assert.Contains("dotnet list backend\\PayPlanner.Api.sln package --vulnerable --include-transitive", qualityGates, StringComparison.Ordinal);
        Assert.Contains("npm audit --audit-level=moderate", qualityGates, StringComparison.Ordinal);

        var surfaceInventory = Path.Combine(root, "docs", "system-surface-inventory.generated.md");
        Assert.True(File.Exists(surfaceInventory));
        var inventory = File.ReadAllText(surfaceInventory);
        Assert.Contains("## Все HTTP endpoints", inventory, StringComparison.Ordinal);
        Assert.Contains("## Фоновые сервисы", inventory, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNotePolicyDocument_DescribesWhatNewRulesAndStorage()
    {
        var root = FindRepositoryRoot();
        var releasePolicy = File.ReadAllText(Path.Combine(root, "docs", "release-note-policy.md"));

        Assert.Contains("backend/AppReleases/releases.json", releasePolicy, StringComparison.Ordinal);
        Assert.Contains("What New", releasePolicy, StringComparison.Ordinal);
        Assert.Contains("user-facing", releasePolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new", releasePolicy, StringComparison.Ordinal);
        Assert.Contains("improved", releasePolicy, StringComparison.Ordinal);
        Assert.Contains("fixed", releasePolicy, StringComparison.Ordinal);
        Assert.Contains("important", releasePolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployRunbook_DocumentsProductionOperationsChecklist()
    {
        var root = FindRepositoryRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "deploy-runbook.md"));

        Assert.Contains("Secrets", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionStrings__Default", runbook, StringComparison.Ordinal);
        Assert.Contains("Jwt__Secret", runbook, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_PUBLIC_KEY", runbook, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_PRIVATE_KEY", runbook, StringComparison.Ordinal);
        Assert.Contains("PAYPLANNER_WEBPUSH_SUBJECT", runbook, StringComparison.Ordinal);
        Assert.Contains("/opt/payplanner/shared/payplanner.env", runbook, StringComparison.Ordinal);
        Assert.Contains("CORS", runbook, StringComparison.Ordinal);
        Assert.Contains("Cors__AllowedOrigins__0", runbook, StringComparison.Ordinal);
        Assert.Contains("TLS", runbook, StringComparison.Ordinal);
        Assert.Contains("HSTS", runbook, StringComparison.Ordinal);
        Assert.Contains("startup migrations", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release sync", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backup", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rollback", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/health", runbook, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = TestRepository.FindDirectory();

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && Directory.Exists(Path.Combine(current.FullName, "backend"))
                && Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
