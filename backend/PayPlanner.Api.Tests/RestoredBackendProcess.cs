using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Own child process with a fresh content root and synthetic restored database only.</summary>
internal sealed class RestoredBackendProcess : IAsyncDisposable
{
    // Bound browser resource use; dependency caches are isolated per owned runtime.
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private readonly Process _process;
    private bool _started;
    private int _browserPort;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly RestoredServerErrorRecorder _errors = new();
    private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HttpClient Client { get; private set; } = null!;

    private RestoredBackendProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, args) => Receive(args.Data);
        process.ErrorDataReceived += (_, args) => Receive(args.Data);
    }

    private void Receive(string? line)
    {
        if (line is null) return;
        _errors.Receive(line);
        _log.Enqueue(line);
        while (_log.Count > 300) _log.TryDequeue(out _);
        var match = Regex.Match(line, @"Now listening on: (http://127\.0\.0\.1:\d+)");
        if (match.Success) _listening.TrySetResult(new Uri(match.Groups[1].Value));
    }

    public static async Task<RestoredBackendProcess> StartAsync(string artifact, string databasePath, string keysPath,
        DatabaseProviderKind providerKind = DatabaseProviderKind.Sqlite, FieldEncryptionKeyMaterial? recoveredFieldKey = null, bool connectionDiagnostics = false)
    {
        var runtimeRoot = Path.GetDirectoryName(artifact)!;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
        var id = json.RootElement.GetProperty("runtimeInstanceId").GetString()!;
        if (providerKind == DatabaseProviderKind.PostgreSql)
        {
            var database = new Npgsql.NpgsqlConnectionStringBuilder(databasePath);
            Assert.Equal("127.0.0.1", database.Host);
            const string prefix = "pp_runtime_target_";
            Assert.StartsWith(prefix, database.Database, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(database.Database![prefix.Length..], "N", out _));
        }
        var apiAssembly = typeof(DatabaseBackupService).Assembly.Location;
        using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        var browserPort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Assert.True(File.Exists(Path.ChangeExtension(apiAssembly, ".runtimeconfig.json")), "API runtime configuration must be built.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = runtimeRoot,
        };
        if (connectionDiagnostics)
        {
            Assert.Equal(DatabaseProviderKind.PostgreSql, providerKind);
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(RestoredConnectionDiagnosticsHostingStartup).Assembly.Location, ".deps.json"));
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(apiAssembly, ".runtimeconfig.json"));
        }
        start.ArgumentList.Add(apiAssembly);
        // Do not inherit database, listener, authentication, or delivery configuration from the user's shell.
        foreach (var key in start.Environment.Keys.ToArray())
            if (new[] { "ASPNETCORE_", "DOTNET_", "Kestrel__", "ConnectionStrings__", "Database__", "DataProtection__", "RestoredRuntimeIsolation__", "Email__", "BackgroundServices__", "Jwt__", "Cors__", "FieldEncryption__", "PP_TEST_RESTORED_FIELD_" }
                .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) start.Environment.Remove(key);
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("PAYPLANNER_SYNTHETIC_", StringComparison.OrdinalIgnoreCase)
                || key.Equals(RestoredConnectionDiagnosticsHostingStartup.EnabledKey, StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        if (connectionDiagnostics)
        {
            start.Environment[RestoredConnectionDiagnosticsHostingStartup.EnabledKey] = "true";
            start.Environment["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = typeof(RestoredConnectionDiagnosticsHostingStartup).Assembly.GetName().Name!;
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_CONTENTROOT"] = runtimeRoot;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        var providerName = providerKind == DatabaseProviderKind.PostgreSql ? "postgresql" : "sqlite";
        start.Environment["PAYPLANNER_DB_PROVIDER"] = providerName;
        start.Environment["Database__Provider"] = providerName;
        start.Environment["ConnectionStrings__Default"] = providerKind == DatabaseProviderKind.PostgreSql
            ? databasePath : $"Data Source={databasePath};Pooling=False";
        start.Environment["BackgroundServices__Enabled"] = "false";
        start.Environment["Email__Enabled"] = "false";
        start.Environment["FieldEncryption__Enabled"] = "false";
        start.Environment["FieldEncryption__ClientPassportDualWriteEnabled"] = "false";
        start.Environment["FieldEncryption__ClientPassportBackfillEnabled"] = "false";
        if (recoveredFieldKey is not null)
        {
            Assert.Equal(DatabaseProviderKind.PostgreSql, providerKind);
            Assert.Equal("synthetic-restore", recoveredFieldKey.KeyId);
            Assert.Equal(32, recoveredFieldKey.MasterKey.Length);
            Assert.Equal(32, recoveredFieldKey.BlindIndexKey.Length);
            // Only synthetic fixture material: memory/environment of this owned child, never a file or user's shell.
            start.Environment["FieldEncryption__Enabled"] = "true";
            start.Environment["FieldEncryption__ActiveKeyId"] = recoveredFieldKey.KeyId;
            start.Environment["FieldEncryption__Keys__synthetic-restore__MasterKeyEnvironmentVariable"] = "PP_TEST_RESTORED_FIELD_MASTER";
            start.Environment["FieldEncryption__Keys__synthetic-restore__BlindIndexKeyEnvironmentVariable"] = "PP_TEST_RESTORED_FIELD_BLIND";
            start.Environment["PP_TEST_RESTORED_FIELD_MASTER"] = Convert.ToBase64String(recoveredFieldKey.MasterKey);
            start.Environment["PP_TEST_RESTORED_FIELD_BLIND"] = Convert.ToBase64String(recoveredFieldKey.BlindIndexKey);
        }
        start.Environment["Cors__AllowedOrigins__0"] = $"http://127.0.0.1:{browserPort}";
        start.Environment["RestoredRuntimeIsolation__Enabled"] = "true";
        start.Environment["RestoredRuntimeIsolation__InstanceId"] = id;
        start.Environment["RestoredRuntimeIsolation__StorageSettingsPath"] = artifact;
        start.Environment["RestoredRuntimeIsolation__KeysPath"] = keysPath;
        foreach (var property in json.RootElement.GetProperty("storageSettings").EnumerateObject())
            start.Environment[property.Name.Replace(":", "__", StringComparison.Ordinal)] = property.Value.GetString();
        var result = new RestoredBackendProcess(new Process { StartInfo = start }) { _browserPort = browserPort };
        try
        {
            result._started = result._process.Start();
            Assert.True(result._started);
            result._process.BeginOutputReadLine();
            result._process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var exited = result._process.WaitForExitAsync(timeout.Token);
            var finished = await Task.WhenAny(result._listening.Task, exited).WaitAsync(timeout.Token);
            Assert.True(finished == result._listening.Task, "Isolated backend failed before listening: " + string.Join(Environment.NewLine, result._log));
            result.Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = await result._listening.Task,
                Timeout = TimeSpan.FromSeconds(20),
            };
            result.Client.DefaultRequestHeaders.Add(RestoredRuntimeRequestGuard.InstanceHeader, id);
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public void AssertNoServerErrors()
    {
        // Failures are retained independently of the bounded startup log tail.
        // The summary includes only categories/types, never message details.
        var summary = _errors.FailureSummary();
        Assert.True(summary.Length == 0, summary);
    }
    public async Task CheckBrowserAsync(string instanceId, string email, string password, int caseId,
        string documentName, byte[] documentBytes)
    {
        Assert.True(await BrowserGate.WaitAsync(TimeSpan.FromMinutes(2)), "Browser verification queue timed out.");
        try
        {
            await CheckBrowserCoreAsync(instanceId, email, password, caseId, documentName, documentBytes);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    private async Task CheckBrowserCoreAsync(string instanceId, string email, string password, int caseId,
        string documentName, byte[] documentBytes)
    {
        var root = TestRepository.FindDirectory();
        while (!File.Exists(Path.Combine(root.FullName, "vite.config.ts"))) root = root.Parent ?? throw new InvalidOperationException("Project root is missing.");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = root.FullName,
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "check-restored-live-browser.mjs"));
        start.Environment["RESTORED_SMOKE_API"] = Client.BaseAddress!.AbsoluteUri;
        start.Environment["RESTORED_SMOKE_ROOT"] = _process.StartInfo.WorkingDirectory;
        start.Environment["RESTORED_SMOKE_INSTANCE"] = instanceId;
        start.Environment["RESTORED_SMOKE_EMAIL"] = email;
        start.Environment["RESTORED_SMOKE_PASSWORD"] = password;
        start.Environment["RESTORED_SMOKE_PORT"] = _browserPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["RESTORED_SMOKE_CASE_ID"] = caseId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["RESTORED_SMOKE_DOCUMENT_NAME"] = documentName;
        start.Environment["RESTORED_SMOKE_DOCUMENT_BASE64"] = Convert.ToBase64String(documentBytes);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser fixture could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            // Forward only bounded counters, never the browser's raw output or restored credentials.
            foreach (var line in (await stdout).Split('\n'))
            {
                var diagnostic = line.Trim();
                if (diagnostic.Length <= 300 && System.Text.RegularExpressions.Regex.IsMatch(diagnostic,
                    "^PP_RESTORED_NOTIFICATION_LIFECYCLE \\{\\\"Started\\\":\\d{1,4},\\\"Finished\\\":\\d{1,4},\\\"FailedBeforeClose\\\":\\d{1,4},\\\"FailedDuringClose\\\":\\d{1,4},\\\"PendingBeforeClose\\\":\\d{1,4}\\}$"))
                    Console.WriteLine(diagnostic);
            }
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Contains("RESTORED_BROWSER_OK", await stdout);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
            throw new TimeoutException($"Restored browser verification exceeded 60 seconds.{Environment.NewLine}{await stderr}{Environment.NewLine}{await stdout}", exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_started && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token);
        }
        _process.Dispose();
    }
}
