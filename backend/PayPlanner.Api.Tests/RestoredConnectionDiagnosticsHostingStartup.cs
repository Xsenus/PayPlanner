using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Microsoft.Data.Sqlite;
using PayPlanner.Api.Services;

[assembly: HostingStartup(typeof(PayPlanner.Api.Tests.RestoredConnectionDiagnosticsHostingStartup))]

namespace PayPlanner.Api.Tests;

// Test assembly only. The real API remains the child entry point.
public sealed class RestoredConnectionDiagnosticsHostingStartup : IHostingStartup
{
    internal const string EnabledKey = "PAYPLANNER_TEST_RESTORED_CONNECTION_DIAGNOSTICS";
    internal const string SyntheticEnabledKey = "PAYPLANNER_SYNTHETIC_CONNECTION_DIAGNOSTICS";
    internal const string SyntheticRestartUrlKey = "PAYPLANNER_SYNTHETIC_RESTART_URL";

    public void Configure(IWebHostBuilder builder)
        => builder.ConfigureServices((context, services) =>
        {
            var restored = Validate(context.Configuration, context.HostingEnvironment.ContentRootPath);
            var synthetic = ValidateSynthetic(context.Configuration, context.HostingEnvironment.ContentRootPath);
            if (!restored && !synthetic) return;
            services.AddHostedService<OwnedConnectionDiagnostics>();
            services.AddSingleton<ILoggerProvider>(provider => new OwnedAuditFailureDiagnostics(
                provider.GetRequiredService<IHttpContextAccessor>()));
            if (synthetic)
            {
                services.AddSingleton<OwnedTaskReadDiagnostics>(provider => new OwnedTaskReadDiagnostics(
                    provider.GetRequiredService<IHttpContextAccessor>()));
                services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<OwnedTaskReadDiagnostics>());
                services.AddScoped<OwnedTaskActionTimingFilter>();
                services.AddHostedService<OwnedTaskQueryTimingObserver>();
                services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
                    options.Filters.Add<OwnedTaskActionTimingFilter>(int.MaxValue));
                services.AddLogging(logging => logging.AddFilter<OwnedTaskReadDiagnostics>(null, LogLevel.Information));
            }
        });

    internal static bool ValidateSynthetic(IConfiguration configuration, string contentRoot)
    {
        var enabled = configuration[SyntheticEnabledKey];
        if (enabled is null or "false") return false;
        if (enabled != "true") throw new InvalidOperationException("Invalid owned synthetic diagnostic activation.");
        if (configuration[RestoredRuntimeIsolation.EnabledKey] != "false"
            || configuration["Email:Enabled"] != "false" || configuration["BackgroundServices:Enabled"] != "false"
            || configuration["Database:Provider"] != "sqlite"
            || !HasOwnedSyntheticListener(configuration))
            throw new InvalidOperationException("Synthetic diagnostics require an owned loopback runtime with mail disabled.");
        var root = Path.GetFullPath(contentRoot);
        var directory = new DirectoryInfo(root);
        const string prefix = "pp_task_upload_browser_";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!directory.Name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(directory.Name[prefix.Length..], "N", out _)
            || !string.Equals(directory.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), temp, comparison))
            throw new InvalidOperationException("Synthetic diagnostics require an owned temporary root.");
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Synthetic diagnostic root must exist without linked directories.");
        var database = new SqliteConnectionStringBuilder(configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("Missing owned synthetic diagnostic database."));
        // Pooling is local to the exact owned database and the API child. It
        // cannot make another file or a shared/non-owned root acceptable.
        if (!Path.IsPathFullyQualified(database.DataSource)
            || database.Mode == SqliteOpenMode.Memory
            || !string.Equals(Path.GetFullPath(database.DataSource), Path.Combine(root, "synthetic.db"), comparison))
            throw new InvalidOperationException("Synthetic diagnostics require the owned disposable SQLite database.");
        return true;
    }

    private static bool HasOwnedSyntheticListener(IConfiguration configuration)
    {
        var listener = configuration[WebHostDefaults.ServerUrlsKey];
        var expected = configuration[SyntheticRestartUrlKey];
        if (expected is null) return listener == "http://127.0.0.1:0";
        return listener == expected
            && Uri.TryCreate(expected, UriKind.Absolute, out var address)
            && address.Scheme == "http" && address.Host == "127.0.0.1"
            && address.Port is > 0 and <= 65535
            && address.AbsolutePath == "/" && address.Query.Length == 0
            && address.Fragment.Length == 0 && address.UserInfo.Length == 0
            && address.GetLeftPart(UriPartial.Authority) == expected;
    }

    internal static bool Validate(IConfiguration configuration, string contentRoot)
    {
        var enabled = configuration[EnabledKey];
        if (enabled is null or "false") return false;
        if (enabled != "true") throw new InvalidOperationException("Invalid owned restore diagnostic activation.");
        if (configuration[RestoredRuntimeIsolation.EnabledKey] != "true"
            || configuration["Email:Enabled"] != "false" || configuration["BackgroundServices:Enabled"] != "false"
            || configuration["Database:Provider"] != "postgresql"
            || configuration[WebHostDefaults.ServerUrlsKey] != "http://127.0.0.1:0")
            throw new InvalidOperationException("Restore diagnostics require an owned isolated loopback runtime with mail disabled.");
        RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration);
        var database = new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("Missing owned restore diagnostic database."));
        const string databasePrefix = "pp_runtime_target_";
        if (database.Host != "127.0.0.1" || database.Database is not { } databaseName
            || !databaseName.StartsWith(databasePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(databaseName[databasePrefix.Length..], "N", out _))
            throw new InvalidOperationException("Restore diagnostics require an owned disposable PostgreSQL database.");
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(Path.GetFullPath(contentRoot));
        while (directory.Parent is not null)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Restore diagnostic root must exist without linked directories.");
            if (string.Equals(directory.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar), temp, StringComparison.OrdinalIgnoreCase))
            {
                const string rootPrefix = "payplanner-runtime-e2e-";
                if (directory.Name.StartsWith(rootPrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(directory.Name[rootPrefix.Length..], "N", out _)) return true;
                break;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Restore diagnostics require an owned temporary runtime root.");
    }

    private sealed class OwnedConnectionDiagnostics : IHostedService, IDisposable,
        IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly object _sync = new();
        private readonly List<IDisposable> _subscriptions = [];
        private bool _disposed;

        private readonly IHttpContextAccessor _httpContext;
        public OwnedConnectionDiagnostics(IHttpContextAccessor httpContext) => _httpContext = httpContext;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_sync) _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
            return Task.CompletedTask;
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name) return;
            lock (_sync)
            {
                if (!_disposed) _subscriptions.Add(listener.Subscribe(this,
                    name => name == RelationalEventId.ConnectionError.Name));
            }
        }

        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Key != RelationalEventId.ConnectionError.Name
                || item.Value is not ConnectionErrorEventData data || data.LogLevel < LogLevel.Error) return;
            // Never invoke EventData.ToString or Exception.Message/ToString: they contain SQL and credentials.
            var action = _httpContext.HttpContext?.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            var operation = action is null ? null : action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name;
            var request = _httpContext.HttpContext;
            string cancellationSource = "unavailable";
            var depth = 0;
            for (Exception? error = data.Exception; error is not null && depth++ < 4; error = error.InnerException)
                if (error is OperationCanceledException cancellation && cancellation.CancellationToken.CanBeCanceled)
                {
                    cancellationSource = request is null ? "unavailable"
                        : cancellation.CancellationToken == request.RequestAborted ? "request" : "other";
                    break;
                }
            Console.Error.WriteLine(Format(data.Exception, data.Duration, operation,
                cancellationSource, request?.RequestAborted.IsCancellationRequested));
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
        }
    }

    internal static string Format(Exception exception, TimeSpan duration, string? requestAction = null,
        string? cancellationSource = null, bool? requestAbortedAtFailure = null)
    {
        static string SafeName(string? name)
            => name is { Length: <= 192 } && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$")
                ? name : "unknown";
        var types = new List<string>();
        var canceled = false;
        for (Exception? current = exception; current is not null && types.Count < 4; current = current.InnerException)
        {
            types.Add(SafeName(current.GetType().FullName));
            canceled |= current is OperationCanceledException;
        }
        var frames = new StackTrace(exception, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.FullName?.StartsWith("PayPlanner.Api.", StringComparison.Ordinal) == true)
            .Take(8).Select(method => SafeName(method!.DeclaringType!.FullName) + "." + SafeName(method.Name)).ToArray();
        return "PP_RESTORED_CONNECTION_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            ExceptionTypes = types,
            Canceled = canceled,
            DurationMilliseconds = Math.Clamp(duration.TotalMilliseconds, 0, 3_600_000),
            ApplicationFrames = frames,
            RequestAction = SafeName(requestAction),
            CancellationSource = cancellationSource is "request" or "other" ? cancellationSource : "unavailable",
            RequestAbortedAtFailure = requestAbortedAtFailure
        });
    }
}
