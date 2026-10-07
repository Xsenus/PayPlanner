using System.Diagnostics;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using PayPlanner.Api.Controllers;

namespace PayPlanner.Api.Tests;

// Test-only observer of selected EF events. Never inspects or formats SQL or query expressions.
internal sealed class OwnedTaskQueryTimingObserver(IHttpContextAccessor accessor, OwnedTaskReadDiagnostics diagnostics)
    : IHostedService, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConditionalWeakTable<DbContext, Pending> _pending = new();
    private readonly ConditionalWeakTable<DbCommand, Pending> _commands = new();
    private readonly ConditionalWeakTable<DbConnection, Pending> _connections = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private bool _stopped;
    private sealed record Pending(HttpContext Request, long Started);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
        return Task.CompletedTask;
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "Microsoft.EntityFrameworkCore") return;
        lock (_gate)
        {
            if (!_stopped) _subscriptions.Add(listener.Subscribe(this, name =>
                name == CoreEventId.QueryCompilationStarting.Name || name == CoreEventId.QueryExecutionPlanned.Name
                || name == RelationalEventId.CommandExecuting.Name || name == RelationalEventId.CommandExecuted.Name
                || name == RelationalEventId.CommandError.Name || name == RelationalEventId.ConnectionOpening.Name
                || name == RelationalEventId.ConnectionOpened.Name || name == RelationalEventId.ConnectionError.Name));
        }
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        if (accessor.HttpContext is not { } request) return;
        var descriptor = request.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (descriptor?.ControllerTypeInfo.AsType() != typeof(TasksController)
            || descriptor.MethodInfo.Name is not (nameof(TasksController.GetTask) or nameof(TasksController.GetSettings)
                or nameof(TasksController.GetFileUploads) or nameof(TasksController.UploadFile))) return;
        if (item.Value is ConnectionEventData connection)
        {
            if (item.Key == RelationalEventId.ConnectionOpening.Name)
            {
                _connections.Remove(connection.Connection);
                _connections.Add(connection.Connection, new(request, Stopwatch.GetTimestamp()));
            }
            else if (_connections.TryGetValue(connection.Connection, out var started))
            {
                _connections.Remove(connection.Connection);
                if (item.Key == RelationalEventId.ConnectionOpened.Name && ReferenceEquals(request, started.Request))
                    diagnostics.ObserveConnectionInterval(request, Stopwatch.GetElapsedTime(started.Started).TotalMilliseconds);
            }
            return;
        }
        if (item.Value is CommandEventData command)
        {
            if (item.Key == RelationalEventId.CommandExecuting.Name)
            {
                _commands.Remove(command.Command);
                _commands.Add(command.Command, new(request, Stopwatch.GetTimestamp()));
            }
            else if (_commands.TryGetValue(command.Command, out var started))
            {
                _commands.Remove(command.Command);
                if (item.Key == RelationalEventId.CommandExecuted.Name && ReferenceEquals(request, started.Request))
                    diagnostics.ObserveCommandInterval(request, Stopwatch.GetElapsedTime(started.Started).TotalMilliseconds);
            }
            return;
        }
        if (item.Value is not QueryExpressionEventData { Context: { } db }) return;
        if (item.Key == CoreEventId.QueryCompilationStarting.Name)
        {
            _pending.Remove(db);
            _pending.Add(db, new(request, Stopwatch.GetTimestamp()));
        }
        else if (item.Key == CoreEventId.QueryExecutionPlanned.Name && _pending.TryGetValue(db, out var pending))
        {
            _pending.Remove(db);
            if (ReferenceEquals(request, pending.Request))
                diagnostics.ObserveCompilation(request, Stopwatch.GetElapsedTime(pending.Started).TotalMilliseconds);
        }
    }

    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
