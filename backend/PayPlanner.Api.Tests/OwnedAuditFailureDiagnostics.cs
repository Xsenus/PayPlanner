using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Services.UserActivity;

namespace PayPlanner.Api.Tests;

// Additional test-only provider. The normal error providers and assertions remain active.
internal sealed class OwnedAuditFailureDiagnostics : ILoggerProvider
{
    internal const string Prefix = "PP_OWNED_AUDIT_DIAGNOSTIC ";
    private readonly IHttpContextAccessor _context;
    private readonly Action<string> _write;

    public OwnedAuditFailureDiagnostics(IHttpContextAccessor context, Action<string>? write = null)
    {
        _context = context;
        _write = write ?? Console.Error.WriteLine;
    }

    public ILogger CreateLogger(string categoryName)
        => categoryName == typeof(UserActivityService).FullName
            ? new AuditLogger(_context, _write) : NullLogger.Instance;

    public void Dispose() { }

    private sealed class AuditLogger(IHttpContextAccessor context, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && exception is not null)
                write(Format(exception, context.HttpContext));
        }
    }

    internal static string Format(Exception exception, HttpContext? request)
    {
        static string SafeName(string? name)
            => name is { Length: <= 192 } && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$")
                ? name : "unknown";
        var types = new List<string>();
        OperationCanceledException? cancellation = null;
        for (Exception? current = exception; current is not null && types.Count < 4; current = current.InnerException)
        {
            types.Add(SafeName(current.GetType().FullName));
            if (current is OperationCanceledException canceled) cancellation ??= canceled;
        }
        var action = request?.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var requestAction = action is null ? null : action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name;
        var token = cancellation?.CancellationToken ?? CancellationToken.None;
        var frames = new StackTrace(exception, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.FullName?.StartsWith("PayPlanner.Api.", StringComparison.Ordinal) == true)
            .Take(8).Select(method => SafeName(method!.DeclaringType!.FullName) + "." + SafeName(method.Name)).ToArray();
        // No message, logging state, URL, SQL, identity or credentials are serialized.
        // Duration is deliberately absent: this provider did not observe the write start.
        return Prefix + JsonSerializer.Serialize(new
        {
            ExceptionTypes = types,
            ApplicationFrames = frames,
            RequestAction = SafeName(requestAction),
            Canceled = cancellation is not null,
            CancellationSource = token.CanBeCanceled && request is not null
                ? token == request.RequestAborted ? "request" : "other" : "unavailable",
            CancellationTokenRequested = token.CanBeCanceled ? (bool?)token.IsCancellationRequested : null,
            RequestAbortedAtFailure = request?.RequestAborted.IsCancellationRequested
        });
    }
}
