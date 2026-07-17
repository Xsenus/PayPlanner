using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services;

public sealed class SlowDbCommandLoggingOptions
{
    public const string SectionName = "Diagnostics:SlowDatabaseCommands";

    public bool Enabled { get; set; } = true;
    public int ThresholdMilliseconds { get; set; } = 500;
}

public sealed class SlowDbCommandInterceptor : DbCommandInterceptor
{
    public const int SlowCommandEventId = 5201;
    public const int SlowFailedCommandEventId = 5202;

    private static readonly EventId SlowCommandEvent = new(SlowCommandEventId, "SlowDatabaseCommand");
    private static readonly EventId SlowFailedCommandEvent = new(SlowFailedCommandEventId, "SlowFailedDatabaseCommand");

    private readonly ILogger<SlowDbCommandInterceptor> _logger;
    private readonly SlowDbCommandLoggingOptions _options;

    public SlowDbCommandInterceptor(
        ILogger<SlowDbCommandInterceptor> logger,
        IOptions<SlowDbCommandLoggingOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        LogIfSlow(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData, failed: false);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        LogIfSlow(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData, failed: false);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        LogIfSlow(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData, failed: false);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        LogIfSlow(command, eventData, failed: true);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData, failed: true);
        return Task.CompletedTask;
    }

    private void LogIfSlow(DbCommand command, CommandEndEventData eventData, bool failed)
    {
        if (!_options.Enabled ||
            eventData.Duration.TotalMilliseconds < Math.Max(1, _options.ThresholdMilliseconds))
        {
            return;
        }

        var operation = ResolveOperation(command.CommandText);
        var fingerprint = BuildFingerprint(command.CommandText);
        var provider = eventData.Context?.Database.ProviderName
            ?? command.Connection?.GetType().Name
            ?? "unknown";
        var contextName = eventData.Context?.GetType().Name ?? "unknown";
        var commandSource = eventData.CommandSource.ToString();

        try
        {
            if (failed)
            {
                _logger.LogWarning(
                    SlowFailedCommandEvent,
                    "Slow database command {Operation} ({CommandSource}, {DatabaseProvider}, {DbContext}) failed after {ElapsedMilliseconds:F2} ms [Fingerprint={CommandFingerprint}].",
                    operation,
                    commandSource,
                    provider,
                    contextName,
                    eventData.Duration.TotalMilliseconds,
                    fingerprint);
            }
            else
            {
                _logger.LogWarning(
                    SlowCommandEvent,
                    "Slow database command {Operation} ({CommandSource}, {DatabaseProvider}, {DbContext}) completed in {ElapsedMilliseconds:F2} ms [Fingerprint={CommandFingerprint}].",
                    operation,
                    commandSource,
                    provider,
                    contextName,
                    eventData.Duration.TotalMilliseconds,
                    fingerprint);
            }
        }
        catch
        {
            // A diagnostics provider must never affect the database command being measured.
        }
    }

    private static string ResolveOperation(string? commandText)
    {
        var span = (commandText ?? string.Empty).AsSpan().TrimStart();
        var length = 0;
        while (length < span.Length && char.IsLetter(span[length]))
        {
            length += 1;
        }

        return length == 0 ? "OTHER" : span[..length].ToString().ToUpperInvariant();
    }

    private static string BuildFingerprint(string? commandText)
    {
        var bytes = Encoding.UTF8.GetBytes(commandText ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
