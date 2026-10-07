using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;

namespace PayPlanner.Api.Tests;

// Test-only numerical observations. Never render structured logging state or SQL.
internal sealed class OwnedTaskReadDiagnostics(IHttpContextAccessor context, Action<string>? write = null,
    TimeProvider? clock = null) : ILoggerProvider
{
    internal const string Prefix = "PP_OWNED_TASK_READ_TIMING ";
    internal const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";
    internal const string ActionCategory = "Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker";
    internal const string RequestCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    internal const string RequestPrefix = "PP_OWNED_TASK_REQUEST_TIMING ";
    private readonly ConditionalWeakTable<HttpContext, QueryCost> _costs = new();
    private readonly Action<string> _write = write ?? Console.WriteLine;
    private int _emitted;
    private int _requestsEmitted;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal sealed record Measurement(string Action, double ActionMilliseconds, int Commands, double CommandMilliseconds, string AtUtc,
        double? HandlerMilliseconds = null, int Compilations = 0, double CompilationMilliseconds = 0,
        double ObservedCommandMilliseconds = 0, [property: JsonPropertyName("ConnectionMs")] double ConnectionMilliseconds = 0);
    internal sealed record RequestMeasurement(
        [property: JsonRequired] string Action, [property: JsonRequired] double RequestMilliseconds,
        [property: JsonRequired] double MvcMilliseconds, [property: JsonRequired] double BeforeMvcMilliseconds,
        [property: JsonRequired] double AfterMvcMilliseconds, [property: JsonRequired] string AtUtc);
    private sealed class QueryCost
    {
        public int Count;
        public double Milliseconds;
        public Measurement? Mvc;
        public long MvcFinished;
        public double? HandlerMilliseconds;
        public int Compilations;
        public double CompilationMilliseconds;
        public double ObservedCommandMilliseconds;
        public double ConnectionMilliseconds;
    }

    public ILogger CreateLogger(string categoryName)
        => categoryName is CommandCategory or ActionCategory or RequestCategory ? new NumericalLogger(this, categoryName) : NullLogger.Instance;

    public void Dispose() { }

    internal void ObserveConnectionInterval(HttpContext request, double milliseconds)
    {
        var descriptor = request.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))
            || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 3_600_000) return;
        var cost = _costs.GetOrCreateValue(request);
        lock (cost) cost.ConnectionMilliseconds = Math.Min(cost.ConnectionMilliseconds + milliseconds, 3_600_000);
    }

    internal void ObserveCommandInterval(HttpContext request, double milliseconds)
    {
        var descriptor = request.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))
            || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 3_600_000) return;
        var cost = _costs.GetOrCreateValue(request);
        lock (cost) cost.ObservedCommandMilliseconds = Math.Min(cost.ObservedCommandMilliseconds + milliseconds, 3_600_000);
    }

    internal void ObserveCompilation(HttpContext request, double milliseconds)
    {
        var descriptor = request.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))
            || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 3_600_000) return;
        var cost = _costs.GetOrCreateValue(request);
        lock (cost)
        {
            cost.Compilations = Math.Min(cost.Compilations + 1, 100_000);
            cost.CompilationMilliseconds = Math.Min(cost.CompilationMilliseconds + milliseconds, 3_600_000);
        }
    }

    internal void ObserveHandler(HttpContext request, double milliseconds)
    {
        var descriptor = request.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))
            || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 3_600_000) return;
        var cost = _costs.GetOrCreateValue(request);
        lock (cost) cost.HandlerMilliseconds = milliseconds;
    }

    internal static Measurement? ReadMeasurement(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length > 512) return null;
        try
        {
            var value = JsonSerializer.Deserialize<Measurement>(line[Prefix.Length..]);
            return value is
            {
                Action: nameof(TasksController.GetTask) or nameof(TasksController.GetSettings) or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile),
                Commands: >= 0 and <= 100_000, ActionMilliseconds: >= 0 and <= 3_600_000,
                CommandMilliseconds: >= 0 and <= 3_600_000, AtUtc.Length: <= 40
            }
                && value.Compilations is >= 0 and <= 100_000
                && double.IsFinite(value.CompilationMilliseconds) && value.CompilationMilliseconds is >= 0 and <= 3_600_000
                && double.IsFinite(value.ObservedCommandMilliseconds) && value.ObservedCommandMilliseconds is >= 0 and <= 3_600_000
                && double.IsFinite(value.ConnectionMilliseconds) && value.ConnectionMilliseconds is >= 0 and <= 3_600_000
                && (value.HandlerMilliseconds is null
                    || double.IsFinite(value.HandlerMilliseconds.Value) && value.HandlerMilliseconds is >= 0 and <= 3_600_000)
                && DateTime.TryParseExact(value.AtUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                ? value : null;
        }
        catch (JsonException) { return null; }
    }

    internal static RequestMeasurement? ReadRequestMeasurement(string line)
    {
        if (!line.StartsWith(RequestPrefix, StringComparison.Ordinal) || line.Length > 512) return null;
        try
        {
            var value = JsonSerializer.Deserialize<RequestMeasurement>(line[RequestPrefix.Length..]);
            return value is
            {
                Action: nameof(TasksController.GetTask) or nameof(TasksController.GetSettings) or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile),
                RequestMilliseconds: >= 0 and <= 3_600_000, MvcMilliseconds: >= 0 and <= 3_600_000,
                BeforeMvcMilliseconds: >= 0 and <= 3_600_000, AfterMvcMilliseconds: >= 0 and <= 3_600_000,
                AtUtc.Length: <= 40
            }
                && Math.Abs(value.RequestMilliseconds - value.MvcMilliseconds
                    - value.BeforeMvcMilliseconds - value.AfterMvcMilliseconds) <= 0.000001
                && DateTime.TryParseExact(value.AtUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class NumericalLogger(OwnedTaskReadDiagnostics owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && exception is null) owner.Observe(category, eventId.Id, state);
        }
    }

    private void Observe<TState>(string category, int eventId, TState state)
    {
        var request = context.HttpContext;
        var descriptor = request?.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (request is null || descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))) return;
        if (category == CommandCategory && eventId == 20101 && TryMilliseconds(state, "elapsed", out var commandTime))
        {
            var cost = _costs.GetOrCreateValue(request);
            lock (cost)
            {
                cost.Count = Math.Min(cost.Count + 1, 100_000);
                cost.Milliseconds = Math.Min(cost.Milliseconds + commandTime, 3_600_000);
            }
        }
        else if (category == ActionCategory && eventId == 105
            && TryMilliseconds(state, "ElapsedMilliseconds", out var actionTime)
            && Interlocked.Increment(ref _emitted) <= 64)
        {
            var cost = _costs.GetOrCreateValue(request);
            Measurement measurement;
            lock (cost)
            {
                measurement = new(descriptor.MethodInfo.Name, actionTime, cost.Count, cost.Milliseconds,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), cost.HandlerMilliseconds,
                    cost.Compilations, cost.CompilationMilliseconds, cost.ObservedCommandMilliseconds, cost.ConnectionMilliseconds);
                cost.Mvc = measurement;
                cost.MvcFinished = _clock.GetTimestamp();
            }
            _write(Prefix + JsonSerializer.Serialize(measurement));
        }
        else if (category == RequestCategory && eventId == 2
            && _costs.TryGetValue(request, out var completed))
        {
            // Hosting RequestFinished runs before HttpContextAccessor is cleared.
            // Event105 measures the MVC pipeline, including filters and results.
            _costs.Remove(request);
            if (!TryMilliseconds(state, "ElapsedMilliseconds", out var requestTime)) return;
            RequestMeasurement measurement;
            lock (completed)
            {
                if (completed.Mvc is not { } mvc) return;
                var after = _clock.GetElapsedTime(completed.MvcFinished, _clock.GetTimestamp()).TotalMilliseconds;
                var before = requestTime - mvc.ActionMilliseconds - after;
                // Reject incoherent clocks/durations rather than inventing a phase.
                if (!double.IsFinite(before) || !double.IsFinite(after)
                    || before is < 0 or > 3_600_000 || after is < 0 or > 3_600_000) return;
                measurement = new(mvc.Action, requestTime, mvc.ActionMilliseconds, before, after,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            if (Interlocked.Increment(ref _requestsEmitted) <= 32)
                _write(RequestPrefix + JsonSerializer.Serialize(measurement));
        }
    }

    private static bool TryMilliseconds<TState>(TState state, string key, out double value)
    {
        value = 0;
        if (state is not IEnumerable<KeyValuePair<string, object?>> entries) return false;
        foreach (var entry in entries)
        {
            if (entry.Key != key) continue;
            var valid = entry.Value switch
            {
                double number => double.IsFinite(value = number),
                string text => text.Length <= 32 && double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out value),
                _ => false,
            };
            return valid && double.IsFinite(value) && value is >= 0 and <= 3_600_000;
        }
        return false;
    }
}
