using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredConnectionDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void SyntheticDiagnosticsStayInactiveWithoutExplicitActivation(string? enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled is not null) settings[RestoredConnectionDiagnosticsHostingStartup.SyntheticEnabledKey] = enabled;
        Assert.False(RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), "not-an-owned-root"));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("invalid")]
    public void SyntheticDiagnosticsRejectInvalidActivation(string enabled)
        => Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(
            Configuration(new() { [RestoredConnectionDiagnosticsHostingStartup.SyntheticEnabledKey] = enabled }), "not-an-owned-root"));

    [Theory]
    [InlineData("Email:Enabled", "true")]
    [InlineData("Email:Enabled", null)]
    [InlineData("BackgroundServices:Enabled", "true")]
    [InlineData("BackgroundServices:Enabled", null)]
    [InlineData("RestoredRuntimeIsolation:Enabled", "true")]
    [InlineData("Database:Provider", "postgresql")]
    [InlineData("urls", "http://0.0.0.0:0")]
    [InlineData("urls", "http://127.0.0.1:8080")]
    [InlineData("ConnectionStrings:Default", "Data Source=:memory:;Pooling=False")]
    [InlineData("ConnectionStrings:Default", "Data Source=synthetic.db;Pooling=False")]
    public void SyntheticDiagnosticsRejectUnsafeSettingsBeforeObservingEvents(string key, string? value)
    {
        using var root = new OwnedRoot("pp_task_upload_browser_");
        var settings = SyntheticSettings(root.Path);
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticDiagnosticsRequireExactOwnedDatabaseAndRootWithoutOpeningDatabase(bool pooling)
    {
        using var root = new OwnedRoot("pp_task_upload_browser_");
        var settings = SyntheticSettings(root.Path);
        settings["ConnectionStrings:Default"] = $"Data Source={Path.Combine(root.Path, "synthetic.db")};Pooling={pooling}";
        Assert.True(RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        settings["ConnectionStrings:Default"] = $"Data Source={Path.Combine(root.Path, "foreign.db")};Pooling={pooling}";
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        using var other = new OwnedRoot("pp_task_upload_browser_");
        settings["ConnectionStrings:Default"] = $"Data Source={Path.Combine(other.Path, "synthetic.db")};Pooling={pooling}";
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        using var foreign = new OwnedRoot();
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(SyntheticSettings(foreign.Path)), foreign.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(foreign.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(other.Path));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080", true)]
    [InlineData("http://127.0.0.1:8080", null, false)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8081", false)]
    [InlineData("http://127.0.0.1:0", "http://127.0.0.1:0", false)]
    [InlineData("http://0.0.0.0:8080", "http://0.0.0.0:8080", false)]
    [InlineData("http://localhost:8080", "http://localhost:8080", false)]
    [InlineData("https://127.0.0.1:8080", "https://127.0.0.1:8080", false)]
    [InlineData("http://127.0.0.1:8080/path", "http://127.0.0.1:8080/path", false)]
    [InlineData("http://127.0.0.1:8080?token=private", "http://127.0.0.1:8080?token=private", false)]
    [InlineData("http://127.0.0.1:8080#fragment", "http://127.0.0.1:8080#fragment", false)]
    [InlineData("http://user:private@127.0.0.1:8080", "http://user:private@127.0.0.1:8080", false)]
    [InlineData("http://127.0.0.1:8080;http://0.0.0.0:8081", "http://127.0.0.1:8080;http://0.0.0.0:8081", false)]
    public void SyntheticRestartRequiresExactExplicitOwnedLoopbackListener(string listener, string? expected, bool accepted)
    {
        using var root = new OwnedRoot("pp_task_upload_browser_");
        var settings = SyntheticSettings(root.Path);
        settings["urls"] = listener;
        if (expected is not null) settings[RestoredConnectionDiagnosticsHostingStartup.SyntheticRestartUrlKey] = expected;
        if (accepted) Assert.True(RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        else Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("Email:Enabled", "true")]
    [InlineData("BackgroundServices:Enabled", "true")]
    [InlineData("RestoredRuntimeIsolation:Enabled", "true")]
    [InlineData("Database:Provider", "postgresql")]
    public void SyntheticRestartRetainsMailAndDatabaseIsolation(string key, string value)
    {
        using var root = new OwnedRoot("pp_task_upload_browser_");
        var settings = SyntheticSettings(root.Path);
        settings["urls"] = "http://127.0.0.1:8080";
        settings[RestoredConnectionDiagnosticsHostingStartup.SyntheticRestartUrlKey] = settings["urls"];
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.ValidateSynthetic(Configuration(settings), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    private static Dictionary<string, string?> SyntheticSettings(string root) => new()
    {
        [RestoredConnectionDiagnosticsHostingStartup.SyntheticEnabledKey] = "true",
        ["RestoredRuntimeIsolation:Enabled"] = "false",
        ["Email:Enabled"] = "false",
        ["BackgroundServices:Enabled"] = "false",
        ["Database:Provider"] = "sqlite",
        ["urls"] = "http://127.0.0.1:0",
        ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(root, "synthetic.db")};Pooling=False",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void OrdinaryHostsDoNotRequireOwnedDatabaseOrRoot(string? enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled is not null) settings[RestoredConnectionDiagnosticsHostingStartup.EnabledKey] = enabled;
        Assert.False(RestoredConnectionDiagnosticsHostingStartup.Validate(Configuration(settings), "not-an-owned-root"));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("invalid")]
    public void InvalidActivationCannotSilentlyEnableDiagnostics(string enabled)
        => Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.Validate(
            Configuration(new() { [RestoredConnectionDiagnosticsHostingStartup.EnabledKey] = enabled }), "not-an-owned-root"));

    [Theory]
    [InlineData("Email:Enabled", "true")]
    [InlineData("Email:Enabled", null)]
    [InlineData("BackgroundServices:Enabled", "true")]
    [InlineData("BackgroundServices:Enabled", null)]
    [InlineData("RestoredRuntimeIsolation:Enabled", "false")]
    [InlineData("RestoredRuntimeIsolation:InstanceId", "invalid")]
    [InlineData("Database:Provider", "sqlite")]
    [InlineData("urls", "http://0.0.0.0:0")]
    [InlineData("urls", "http://127.0.0.1:8080")]
    [InlineData("ConnectionStrings:Default", "Host=remote.example;Database=pp_runtime_target_00000000000000000000000000000001")]
    [InlineData("ConnectionStrings:Default", "Host=127.0.0.1;Database=production")]
    [InlineData("ConnectionStrings:Default", "Host=127.0.0.1;Database=pp_runtime_target_invalid")]
    public void ActivationRejectsUnsafeRuntimeBeforeObservingEvents(string key, string? value)
    {
        using var root = new OwnedRoot();
        var settings = Settings();
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.Validate(Configuration(settings), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public void ActivationRequiresOwnedTemporaryRoot()
    {
        using var root = new OwnedRoot("pp_unowned_restore_diagnostics_");
        Assert.Throws<InvalidOperationException>(() => RestoredConnectionDiagnosticsHostingStartup.Validate(Configuration(Settings()), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public void SafeOwnedRuntimeCanEnableDiagnosticsWithoutOpeningDatabase()
    {
        using var root = new OwnedRoot();
        Assert.True(RestoredConnectionDiagnosticsHostingStartup.Validate(Configuration(Settings()), root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public void FormatterRetainsOnlyBoundedTypesAndCancellationWithoutReadingSecretDetails()
    {
        var error = new DetailMustNotBeReadException(new TaskCanceledException("synthetic-sql-password-secret"));
        var result = RestoredConnectionDiagnosticsHostingStartup.Format(error, TimeSpan.FromSeconds(2),
            "PayPlanner.Api.Controllers.UserNotificationsController.GetMine");
        Assert.Contains(nameof(DetailMustNotBeReadException), result);
        Assert.Contains(nameof(TaskCanceledException), result);
        Assert.Contains("\"Canceled\":true", result);
        Assert.Contains("\"DurationMilliseconds\":2000", result);
        Assert.Contains("UserNotificationsController.GetMine", result);
        Assert.DoesNotContain("synthetic-sql-password-secret", result);
        Assert.True(result.Length < 4096);
    }

    [Fact]
    public void FormatterRejectsRequestDetailsOutsideTrustedSymbolSyntax()
    {
        var result = RestoredConnectionDiagnosticsHostingStartup.Format(new InvalidOperationException(), TimeSpan.Zero,
            "GET /private?token=synthetic-private-secret", "synthetic-private-source-secret", true);
        Assert.DoesNotContain("synthetic-private-secret", result);
        Assert.Contains("\"RequestAction\":\"unknown\"", result);
        Assert.Contains("\"CancellationSource\":\"unavailable\"", result);
        Assert.Contains("\"RequestAbortedAtFailure\":true", result);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> Settings() => new()
    {
        [RestoredConnectionDiagnosticsHostingStartup.EnabledKey] = "true",
        ["Email:Enabled"] = "false",
        ["BackgroundServices:Enabled"] = "false",
        ["RestoredRuntimeIsolation:Enabled"] = "true",
        ["RestoredRuntimeIsolation:InstanceId"] = Guid.NewGuid().ToString("N"),
        ["Database:Provider"] = "postgresql",
        ["urls"] = "http://127.0.0.1:0",
        ["ConnectionStrings:Default"] = new NpgsqlConnectionStringBuilder
        { Host = "127.0.0.1", Database = "pp_runtime_target_" + Guid.NewGuid().ToString("N") }.ToString()
    };

    private sealed class DetailMustNotBeReadException(Exception inner) : Exception("synthetic-private-detail", inner)
    {
        public override string Message => throw new InvalidOperationException("Exception.Message must not be read.");
        public override string ToString() => throw new InvalidOperationException("Exception.ToString must not be read.");
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = string.Empty;
        public OwnedRoot(string prefix = "payplanner-runtime-e2e-")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => Directory.Delete(Path);
    }
}
