using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Ordinary writable runtime, exclusively on an owned synthetic database. Not a restored clone.</summary>
internal sealed class SyntheticWritableBackendProcess : IAsyncDisposable
{
    private const string Prefix = "pp_task_upload_browser_";
    private readonly ConcurrentQueue<string> _log = new();
    private readonly RestoredServerErrorRecorder _errors = new();
    private readonly ConcurrentQueue<OwnedTaskReadDiagnostics.Measurement> _taskReadTimings = new();
    private int _taskReadTimingsReceived;
    private readonly ConcurrentQueue<OwnedTaskReadDiagnostics.RequestMeasurement> _taskRequestTimings = new();
    private int _taskRequestTimingsReceived;
    private TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process _process;
    private bool _started;
    private readonly int _browserPort;
    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "synthetic.db");
    public string Email { get; } = "synthetic-admin@payplanner.local";
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA!1";
    public HttpClient Client { get; private set; } = null!;
    public IConfiguration Configuration { get; }

    private SyntheticWritableBackendProcess(bool workerEnabled, LoopbackS3Endpoint? secondCopy, bool capacityChecks, bool uploadCapacityChecks,
        LoopbackYandexDiskEndpoint? yandexCopy, bool courtEnabled, bool localAckFault, bool chatAttachments)
    {
        Root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        using var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        _browserPort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root,
        };
        // The entry point remains the real API. Test deps only make the isolated
        // hosting-startup assembly resolvable in this owned child process.
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(SyntheticYandexHostingStartup).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(DatabaseBackupService).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add(typeof(DatabaseBackupService).Assembly.Location);
        // Whitelist OS prerequisites only. In particular, never inherit production provider/SMTP secrets.
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_CONTENTROOT"] = Root,
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = typeof(RestoredConnectionDiagnosticsHostingStartup).Assembly.GetName().Name!,
            [RestoredConnectionDiagnosticsHostingStartup.SyntheticEnabledKey] = "true",
            ["Cors__AllowedOrigins__0"] = $"http://127.0.0.1:{_browserPort}",
            ["Database__Provider"] = "sqlite",
            ["PAYPLANNER_DB_PROVIDER"] = "sqlite",
            ["ConnectionStrings__Default"] = $"Data Source={DatabasePath};Pooling=False",
            ["BackgroundServices__Enabled"] = "false",
            ["Email__Enabled"] = "false",
            ["FieldEncryption__Enabled"] = "false",
            ["FieldEncryption__ClientPassportDualWriteEnabled"] = "false",
            ["FieldEncryption__ClientPassportBackfillEnabled"] = "false",
            ["RestoredRuntimeIsolation__Enabled"] = "false",
            ["PAYPLANNER_SKIP_CLIENT_SEED"] = "1",
            ["Seed__ClientsAndPayments"] = "false",
            ["PAYPLANNER_ADMIN_EMAIL"] = Email,
            ["PAYPLANNER_ADMIN_PASSWORD"] = Password,
            ["PAYPLANNER_ADMIN_FULLNAME"] = "Synthetic browser administrator",
            ["Jwt__Secret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Jwt__Issuer"] = "synthetic-writable-runtime",
            ["Jwt__Audience"] = "synthetic-writable-runtime",
            ["DataProtection__KeysPath"] = Path.Combine(Root, "keys"),
            ["DataProtection__ApplicationName"] = Path.GetFileName(Root),
            ["StorageOrchestration__Enabled"] = "true",
            ["StorageOrchestration__MigrationCapacityChecksEnabled"] = capacityChecks ? "true" : "false",
            ["StorageOrchestration__UploadCapacityChecksEnabled"] = uploadCapacityChecks ? "true" : "false",
            ["StorageOrchestration__UploadMinimumFreeBytes"] = "0",
            ["StorageOrchestration__AutoTaskFilePublicationEnabled"] = "true",
            ["StorageOrchestration__WorkerEnabled"] = workerEnabled ? "true" : "false",
            ["StorageOrchestration__LocalRoot"] = Path.Combine(Root, "objects"),
            ["FileStorage__RootPath"] = Path.Combine(Root, "files"),
            ["StorageOrchestration__ProductReadEnabledClasses__0"] = "task-file",
            ["StorageOrchestration__ProductReadEnabledClasses__1"] = "case-document",
            ["StorageOrchestration__ProductWriteEnabledClasses__0"] = "task-file",
            ["StorageOrchestration__ProductWriteEnabledClasses__1"] = "case-document",
        };
        if (chatAttachments)
        {
            settings["StorageOrchestration__ProductReadEnabledClasses__2"] = "chat-attachment";
            settings["StorageOrchestration__ProductWriteEnabledClasses__2"] = "chat-attachment";
        }
        if (secondCopy is not null)
        {
            Assert.Equal("127.0.0.1", secondCopy.Address.Host);
            Assert.Equal("http", secondCopy.Address.Scheme);
            settings["StorageOrchestration__DefaultPolicy__RequiredCopies"] = "2";
            settings["StorageOrchestration__DefaultPolicy__WriteProviders__0"] = "local";
            settings["StorageOrchestration__DefaultPolicy__WriteProviders__1"] = "s3";
            settings["StorageOrchestration__DefaultPolicy__ReadProviders__0"] = "local";
            settings["StorageOrchestration__DefaultPolicy__ReadProviders__1"] = "s3";
            settings["StorageOrchestration__ProviderConnections__s3"] = LoopbackS3Endpoint.ConnectionId;
            settings["S3Storage__EndpointSecurity__AllowHttp"] = "true";
            settings["S3Storage__EndpointSecurity__AllowedHosts__0"] = "127.0.0.1";
        }
        if (yandexCopy is not null)
        {
            Assert.Null(secondCopy);
            Assert.Equal("127.0.0.1", yandexCopy.Address.Host);
            Assert.Equal("http", yandexCopy.Address.Scheme);
            settings["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = typeof(SyntheticYandexHostingStartup).Assembly.GetName().Name!;
            settings[SyntheticYandexHostingStartup.EnabledKey] = "true";
            settings[SyntheticYandexHostingStartup.EndpointKey] = yandexCopy.Address.AbsoluteUri;
            settings[SyntheticYandexHostingStartup.WorkerKey] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            settings["StorageOrchestration__DefaultPolicy__RequiredCopies"] = "2";
            settings["StorageOrchestration__DefaultPolicy__WriteProviders__0"] = "local";
            settings["StorageOrchestration__DefaultPolicy__WriteProviders__1"] = "yandex-disk";
            settings["StorageOrchestration__DefaultPolicy__ReadProviders__0"] = "local";
            settings["StorageOrchestration__DefaultPolicy__ReadProviders__1"] = "yandex-disk";
            settings["StorageOrchestration__ProviderConnections__yandex-disk"] = "default";
            settings["StorageOrchestration__AutoCaseDocumentPublicationEnabled"] = "true";
        }
        if (courtEnabled)
        {
            Assert.NotNull(yandexCopy);
            settings[SyntheticYandexHostingStartup.CourtKey] = "true";
            settings["FeatureFlags__Legal__ExternalCourtSync"] = "true";
        }
        if (localAckFault)
        {
            Assert.NotNull(yandexCopy); Assert.True(uploadCapacityChecks);
            settings[SyntheticYandexHostingStartup.LocalFaultKey] = "true";
        }
        foreach (var (key, value) in settings) start.Environment[key] = value;
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(entry =>
            new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value))).Build();
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, args) => Receive(args.Data);
        _process.ErrorDataReceived += (_, args) => Receive(args.Data);
    }

    public PaymentContext OpenDatabase() => new(new DbContextOptionsBuilder<PaymentContext>()
        .UseSqlite($"Data Source={DatabasePath};Pooling=False").UsePayPlannerSqliteMigrations().Options);

    public async Task<PayPlanner.Api.Services.Storage.StorageOperationBatchResult> RunOwnedYandexWorkerAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SyntheticYandexHostingStartup.WorkerPath);
        request.Headers.Add(SyntheticYandexHostingStartup.WorkerHeader, Configuration[SyntheticYandexHostingStartup.WorkerKey]
            ?? throw new InvalidOperationException("Runtime has no owned Yandex worker."));
        using var response = await Client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.StorageOperationBatchResult>(response.Content))!;
    }

    public async Task RestartAsync(TimeSpan outage)
    {
        Assert.InRange(outage, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        Assert.True(_started && !_process.HasExited);
        Assert.Equal("false", _process.StartInfo.Environment["BackgroundServices__Enabled"]);
        Assert.Equal("false", _process.StartInfo.Environment["Email__Enabled"]);
        await using (var db = OpenDatabase())
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                .Select(s => s.Value).SingleAsync());
        var address = Client.BaseAddress!;
        Assert.Equal("127.0.0.1", address.Host);
        var previousId = _process.Id;
        var start = _process.StartInfo;
        _process.Kill(entireProcessTree: true);
        using (var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await _process.WaitForExitAsync(stopped.Token);
        // Drain the old process's redirected output before resetting the listening proof.
        _process.WaitForExit();
        _process.Dispose();
        _started = false;
        if (outage > TimeSpan.Zero) await Task.Delay(outage);
        _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartUrl = address.GetLeftPart(UriPartial.Authority);
        start.Environment[RestoredConnectionDiagnosticsHostingStartup.SyntheticRestartUrlKey] = restartUrl;
        start.Environment["ASPNETCORE_URLS"] = restartUrl;
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, args) => Receive(args.Data);
        _process.ErrorDataReceived += (_, args) => Receive(args.Data);
        _started = _process.Start();
        Assert.True(_started);
        Assert.NotEqual(previousId, _process.Id);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var exited = _process.WaitForExitAsync(timeout.Token);
        var completed = await Task.WhenAny(_listening.Task, exited).WaitAsync(timeout.Token);
        if (completed == exited)
        {
            // Observe cancellation as a timeout, not as a successful process exit.
            await exited;
            Assert.Fail($"Owned restarted backend exited before listening (exit{_process.ExitCode}). " + _errors.FailureSummary());
        }
        Assert.True(completed == _listening.Task);
        Assert.Equal(address, await _listening.Task);
    }

    public static async Task<SyntheticWritableBackendProcess> StartAsync(string cardMode = "legacy", bool workerEnabled = false,
        LoopbackS3Endpoint? secondCopy = null, bool capacityChecks = false, bool uploadCapacityChecks = false,
        LoopbackYandexDiskEndpoint? yandexCopy = null, SudrfCourtCaseData? courtSource = null, bool localAckFault = false,
        bool chatAttachments = false)
    {
        Assert.Contains(cardMode, new[] { "legacy", "modern" });
        var result = new SyntheticWritableBackendProcess(workerEnabled, secondCopy, capacityChecks, uploadCapacityChecks, yandexCopy, courtSource is not null, localAckFault, chatAttachments);
        try
        {
            if (courtSource is not null) await result.SetCourtSourceAsync(courtSource);
            // Explicit database override exists BEFORE the backend can instantiate notification services.
            await using (var db = result.OpenDatabase())
            {
                await db.Database.MigrateAsync();
                db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                db.SystemSettings.Add(new SystemSetting { Key = "tasks.taskCardWindowMode", Value = cardMode });
                await db.SaveChangesAsync();
                if (yandexCopy is not null)
                {
                    db.SystemSettings.AddRange(new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = LoopbackYandexDiskEndpoint.Token },
                        new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = LoopbackYandexDiskEndpoint.RootPath });
                    await db.SaveChangesAsync();
                }
                if (secondCopy is not null)
                {
                    var keysPath = Path.Combine(result.Root, "keys");
                    Directory.CreateDirectory(keysPath);
                    var protection = DataProtectionProvider.Create(new DirectoryInfo(keysPath), builder => builder.SetApplicationName(Path.GetFileName(result.Root)));
                    var security = new S3EndpointSecurityPolicy(new SyntheticFixedOptions<S3EndpointSecurityOptions>(new()
                    { AllowHttp = true, AllowedHosts = ["127.0.0.1"] }));
                    var service = new S3StorageService(db, new S3StorageClientFactory(result.Configuration), protection, security);
                    var connection = secondCopy.Connection;
                    await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
                    {
                        Id = connection.Id,
                        Name = connection.Name,
                        Provider = connection.Provider,
                        Endpoint = connection.Endpoint,
                        Region = connection.Region,
                        Bucket = connection.Bucket,
                        RootPrefix = connection.RootPrefix,
                        AccessKeyId = connection.AccessKeyId,
                        SecretAccessKey = connection.SecretAccessKey,
                        IsEnabled = true,
                    });
                }
            }
            result._started = result._process.Start();
            Assert.True(result._started);
            result._process.BeginOutputReadLine();
            result._process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var exited = result._process.WaitForExitAsync(timeout.Token);
            var completed = await Task.WhenAny(result._listening.Task, exited).WaitAsync(timeout.Token);
            Assert.True(completed == result._listening.Task, "Synthetic writable backend failed before listening: "
                + string.Join(Environment.NewLine, result._log));
            result.Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = await result._listening.Task,
                Timeout = TimeSpan.FromSeconds(20),
            };
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public Task SetCourtSourceAsync(SudrfCourtCaseData source)
    {
        Assert.Equal("true", Configuration[SyntheticYandexHostingStartup.CourtKey]);
        Assert.Equal(SyntheticYandexHostingStartup.CourtUrl, source.SourceUrl);
        Assert.All(source.CourtActs, act => Assert.Equal(source.SourceUrl, act.SourceUrl));
        return File.WriteAllTextAsync(Path.Combine(Root, SyntheticYandexHostingStartup.CourtFile),
            System.Text.Json.JsonSerializer.Serialize(source));
    }

    private void Receive(string? line)
    {
        if (line is null) return;
        _errors.Receive(line);
        if (OwnedTaskReadDiagnostics.ReadMeasurement(line) is { } timing
            && Interlocked.Increment(ref _taskReadTimingsReceived) <= 64)
            _taskReadTimings.Enqueue(timing);
        if (OwnedTaskReadDiagnostics.ReadRequestMeasurement(line) is { } requestTiming
            && Interlocked.Increment(ref _taskRequestTimingsReceived) <= 32)
            _taskRequestTimings.Enqueue(requestTiming);
        _log.Enqueue(line);
        while (_log.Count > 300) _log.TryDequeue(out _);
        var match = Regex.Match(line, @"Now listening on: (http://127\.0\.0\.1:\d+)");
        if (match.Success) _listening.TrySetResult(new Uri(match.Groups[1].Value));
    }

    public void AssertNoServerErrors()
    {
        var diagnostic = ServerFailureDiagnostic();
        Assert.True(string.IsNullOrEmpty(diagnostic), diagnostic);
    }

    private string ServerFailureDiagnostic()
        => _errors.FailureSummary();

    private static DirectoryInfo FindRepositoryDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        // Testhost may start beside relocated artifacts. Prefer the compiled
        // source location, retaining invocation and assembly ancestry fallbacks.
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), Directory.GetCurrentDirectory(), Path.GetDirectoryName(typeof(DatabaseBackupService).Assembly.Location) })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            for (DirectoryInfo? repository = new(start); repository is not null; repository = repository.Parent)
                if (File.Exists(Path.Combine(repository.FullName, "vite.config.ts"))
                    && File.Exists(Path.Combine(repository.FullName, "backend", "PayPlanner.Api.csproj")))
                    return repository;
        }
        throw new InvalidOperationException("Project root is missing.");
    }

    public async Task CheckStorageCapacityBrowserAsync(string token, string? changedStagePath = null, bool releasedMarkers = false)
    {
        var repository = FindRepositoryDirectory();
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository.FullName,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
        start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", "check-storage-capacity-visual.mjs"));
        start.Environment["STORAGE_CAPACITY_API"] = Client.BaseAddress!.AbsoluteUri;
        start.Environment["STORAGE_CAPACITY_BROWSER_PORT"] = _browserPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["STORAGE_CAPACITY_TOKEN"] = token;
        if (releasedMarkers) start.Environment["STORAGE_CAPACITY_MARKERS"] = "true";
        if (changedStagePath is not null)
        {
            var expected = Path.Combine(Root, "objects", ".task-uploads", "changed.tmp");
            Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(changedStagePath));
            start.Environment["STORAGE_CAPACITY_WRITER_ROOT"] = Root;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser fixture could not start.");
        var phases = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var receivedPhases = 0;
        var output = ReadOutputAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var pipes = Task.WhenAll(output, errors);
        ObservePipeFailure(output);
        ObservePipeFailure(errors);
        ObservePipeFailure(pipes);
        Exception? primaryFailure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            await pipes.WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, "Storage capacity browser exited unsuccessfully. " + PhaseSummary());
            Assert.True((await output).Contains("Storage capacity owned backend browser check passed", StringComparison.Ordinal),
                "Storage capacity browser did not report its required success marker. " + PhaseSummary());
        }
        catch (OperationCanceledException error)
        {
            primaryFailure = new TimeoutException("Storage capacity browser exceeded its existing 90-second limit. " + PhaseSummary(), error);
            throw primaryFailure;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cleanupTimeout.Token);
                await pipes.WaitAsync(cleanupTimeout.Token);
            }
            catch (Exception) when (primaryFailure is not null)
            {
                // Keep the scenario failure. Pipe faults are observed even if
                // a killed process cannot finish draining in the cleanup budget.
            }
        }

        string PhaseSummary()
            => phases.IsEmpty ? "No validated phase markers were received."
                : "Validated browser phases:" + Environment.NewLine + string.Join(Environment.NewLine, phases.ToArray());

        async Task<string> ReadOutputAsync()
        {
            var captured = new System.Text.StringBuilder();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                captured.AppendLine(line);
                if (line.Length <= 400 && IsSafePhase(line) && Interlocked.Increment(ref receivedPhases) <= 64)
                    phases.Enqueue(line);
            }
            return captured.ToString();
        }

        static void ObservePipeFailure(Task task)
            => _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        static bool IsSafePhase(string line)
            => Regex.IsMatch(line,
                @"\APP_STORAGE_CAPACITY_BROWSER_PHASE \{""Phase"":""(?:module-ready|create-server|vite-listen|entry-transform|launch-msedge|launch-chrome|launch-chromium|browser-context|new-page|fixture-navigation|preview-assertions|screenshots-directory|viewport-1280|screenshot-1280|viewport-390|screenshot-390|writer-create|apply-click|apply-response|apply-json|apply-view|final-assertions|browser-close|vite-close|done)"",""Event"":""(?:enter|failed|attempt-failed)"",""ElapsedMs"":[0-9]{1,9},""ProcessUptimeMs"":[0-9]{1,9},""BrowserErrors"":[0-9]{1,9},""OutsideRequests"":[0-9]{1,9},""Screenshots"":[0-9]{1,9},""ErrorKind"":""(?:none|timeout|assertion|error|unknown)""\}\z",
                RegexOptions.CultureInvariant);
    }

    public async Task CheckUploadBrowserAsync(int taskId, string cardMode, Func<Task>? completeQueue = null, string flow = "replay", Uri? apiAddress = null, bool delayCardSettings = false, int? completionUserId = null)
    {
        var repository = FindRepositoryDirectory();
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = completeQueue is not null,
            WorkingDirectory = repository.FullName,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
        start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", "check-task-upload-live-browser.mjs"));
        start.Environment["TASK_UPLOAD_API"] = (apiAddress ?? Client.BaseAddress!).AbsoluteUri;
        start.Environment["TASK_UPLOAD_ROOT"] = Root;
        start.Environment["TASK_UPLOAD_EMAIL"] = Email;
        start.Environment["TASK_UPLOAD_PASSWORD"] = Password;
        start.Environment["TASK_UPLOAD_PORT"] = _browserPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["TASK_UPLOAD_ID"] = taskId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["TASK_UPLOAD_CARD_MODE"] = cardMode;
        start.Environment["TASK_UPLOAD_QUEUE_REPLAY"] = completeQueue is not null ? "true" : "false";
        start.Environment["TASK_UPLOAD_FLOW"] = flow;
        start.Environment["TASK_UPLOAD_DELAY_CARD_SETTINGS"] = delayCardSettings ? "true" : "false";
        if (flow is "completion" or "completionSaveFailure")
        {
            Assert.True(completionUserId is > 0, "Completion preferences require the authenticated synthetic user.");
            start.Environment["TASK_UPLOAD_COMPLETION_USER_ID"] = completionUserId.GetValueOrDefault()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser fixture could not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var latestStage = "starting";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var stdout = ReadOutputAsync(timeout.Token);
            await Task.WhenAll(stdout, process.WaitForExitAsync(timeout.Token));
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Contains("TASK_UPLOAD_BROWSER_OK", await stdout);

            async Task<string> ReadOutputAsync(CancellationToken ct)
            {
                var output = new System.Text.StringBuilder();
                var completed = false;
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    output.AppendLine(line);
                    latestStage = line;
                    if (line != "TASK_UPLOAD_QUEUE_READY") continue;
                    Assert.NotNull(completeQueue);
                    Assert.False(completed, "The owned queue must only be driven once.");
                    completed = true;
                    await completeQueue();
                    await process.StandardInput.WriteLineAsync("QUEUE_COMPLETED");
                    await process.StandardInput.FlushAsync(ct);
                    process.StandardInput.Close();
                }
                if (completeQueue is not null) Assert.True(completed, "The browser never closed the pending card. " + await stderr);
                return output.ToString();
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Synthetic browser timed out at: " + latestStage);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
    }

    public async Task CheckCaseRetirementBrowserAsync(int caseId, Guid uploadKey, bool lostAcknowledgement, string flow = "documents",
        int clientId = 0, int sourceDocumentId = 0, string? documentKey = null, Func<Task>? applyBoundary = null,
        bool generatedDocuments = false, bool courtDocuments = false, bool financialDocuments = false, bool chatAttachments = false)
    {
        var repository = FindRepositoryDirectory();
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = applyBoundary is not null,
            WorkingDirectory = repository.FullName,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE", "HOME", "XDG_CACHE_HOME", "PLAYWRIGHT_BROWSERS_PATH" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
        if (Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PLAYWRIGHT_CLI") is { } cliPath)
            start.Environment["CASE_RETIREMENT_CLI"] = cliPath;
        if (Environment.GetEnvironmentVariable("PAYPLANNER_TEST_BROWSER") is { } browser)
            start.Environment["CASE_RETIREMENT_BROWSER"] = browser;
        start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", chatAttachments ? "check-chat-managed-live-browser.mjs"
            : financialDocuments ? "check-case-financial-live-browser.mjs"
            : courtDocuments ? "check-case-court-live-browser.mjs"
            : generatedDocuments ? "check-case-generated-live-browser.mjs" : "check-case-retirement-live-browser.mjs"));
        start.Environment["CASE_RETIREMENT_API"] = Client.BaseAddress!.AbsoluteUri;
        start.Environment["CASE_RETIREMENT_ROOT"] = Root;
        start.Environment["CASE_RETIREMENT_EMAIL"] = Email;
        start.Environment["CASE_RETIREMENT_PASSWORD"] = Password;
        start.Environment["CASE_RETIREMENT_PORT"] = _browserPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CASE_RETIREMENT_ID"] = caseId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CASE_RETIREMENT_KEY"] = uploadKey.ToString("D");
        start.Environment["CASE_RETIREMENT_LOST_ACK"] = lostAcknowledgement ? "true" : "false";
        start.Environment["CASE_RETIREMENT_FLOW"] = flow;
        start.Environment["CASE_RETIREMENT_CLIENT_ID"] = clientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CASE_RETIREMENT_SOURCE_DOCUMENT_ID"] = sourceDocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CASE_RETIREMENT_DOCUMENT_KEY"] = documentKey ?? "";
        start.Environment["CASE_RETIREMENT_BOUNDARY"] = applyBoundary is not null ? "true" : "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser fixture could not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var stdout = ReadOutputAsync(timeout.Token);
            await Task.WhenAll(stdout, process.WaitForExitAsync(timeout.Token));
            Assert.True(process.ExitCode == 0, process.ExitCode == 0 ? null
                : await stdout + Environment.NewLine + await stderr + Environment.NewLine + string.Join(Environment.NewLine, _log));
            Assert.Contains("CASE_RETIREMENT_BROWSER_OK", await stdout);

            async Task<string> ReadOutputAsync(CancellationToken ct)
            {
                var output = new System.Text.StringBuilder();
                var applied = false;
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    output.AppendLine(line);
                    if (chatAttachments && Regex.IsMatch(line,
                        @"\APP_CHAT_BROWSER_(?:PHASE (?:ui-build|preview-listen|pending-scenario|apply-boundary|published-scenario|browser-close)|READS phase=(?:before-lost-reload|before-draft-reload|pending|published|before-close) started=\d{1,6} finished=\d{1,6} failed=\d{1,6} locallyAborted=\d{1,6} pending=\d{1,6}|POST phase=(?:start|end|fail) elapsedMs=\d{1,9} apiReadsStarted=\d{1,6} apiReadsFinished=\d{1,6} apiReadsFailed=\d{1,6} apiReadsPending=\d{1,6})\z",
                        RegexOptions.CultureInvariant)) Console.WriteLine(line);
                    if (line != "CASE_RETIREMENT_BOUNDARY_READY") continue;
                    Assert.NotNull(applyBoundary);
                    Assert.False(applied, "The owned boundary must be applied exactly once.");
                    applied = true;
                    await applyBoundary();
                    await process.StandardInput.WriteLineAsync("BOUNDARY_APPLIED");
                    await process.StandardInput.FlushAsync(ct);
                    // Vite treats stdin EOF as parent termination. Keep this owned pipe open until exit.
                }
                if (applyBoundary is not null && !applied)
                {
                    var diagnostic = (output + Environment.NewLine + await stderr)
                        .Replace(Password, "<password>", StringComparison.Ordinal)
                        .Replace(Email, "<email>", StringComparison.Ordinal);
                    Assert.True(applied, "The browser never reached the owned boundary." + Environment.NewLine + diagnostic
                        + Environment.NewLine + ServerFailureDiagnostic());
                }
                return output.ToString();
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
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
        var fullRoot = Path.GetFullPath(Root);
        var name = Path.GetFileName(fullRoot);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(fullRoot), ignoreCase: true);
        Assert.StartsWith(Prefix, name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(name[Prefix.Length..], "N", out _));
        // SQLite's Windows WAL handles can be released shortly after the owned process exits.
        // Retry only this validated root; never clear pools belonging to other tests/projects.
        for (var attempt = 0; Directory.Exists(fullRoot); attempt++)
        {
            try { Directory.Delete(fullRoot, recursive: true); }
            catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException)
            { await Task.Delay(200); }
        }
        // Record diagnostics only after owned resources have been released, even if artifact I/O fails.
        if (!_taskReadTimings.IsEmpty || !_taskRequestTimings.IsEmpty)
        {
            var repository = FindRepositoryDirectory();
            var artifacts = Path.Combine(repository.FullName, ".playwright-cli");
            Directory.CreateDirectory(artifacts);
            if (!_taskReadTimings.IsEmpty)
            {
                await using var stream = new FileStream(Path.Combine(artifacts,
                    "task-server-read-timings-" + name + ".json"), FileMode.CreateNew);
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, _taskReadTimings.ToArray());
            }
            if (!_taskRequestTimings.IsEmpty)
            {
                await using var stream = new FileStream(Path.Combine(artifacts,
                    "task-server-request-timings-" + name + ".json"), FileMode.CreateNew);
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, _taskRequestTimings.ToArray());
            }
        }
    }
}

internal sealed class SyntheticFixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
