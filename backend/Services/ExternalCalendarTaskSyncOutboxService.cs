using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Добавляет изменения задач в сохраняемую очередь внешних календарей.
/// </summary>
public interface IExternalCalendarTaskSyncOutbox
{
    /// <summary>
    /// Фиксирует новое состояние задачи для последующей фоновой передачи.
    /// Метод возвращается после записи в БД и не ожидает внешнюю сеть.
    /// </summary>
    Task EnqueueAsync(
        int taskId,
        string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Будит обработчик очереди после появления новой записи.
/// Сигнал не является единственным способом запуска: worker также выполняет
/// периодическую проверку, поэтому перезапуск процесса не теряет поручения.
/// </summary>
public sealed class ExternalCalendarTaskSyncWakeSignal
{
    private readonly object _lock = new();
    private TaskCompletionSource _pending = NewSignal();

    /// <summary>
    /// Завершает текущее ожидание worker и сразу создаёт сигнал для следующего цикла.
    /// </summary>
    public void Pulse()
    {
        lock (_lock)
        {
            _pending.TrySetResult();
            _pending = NewSignal();
        }
    }

    /// <summary>
    /// Ожидает новое поручение либо истечение контрольного интервала.
    /// </summary>
    public Task WaitAsync(TimeSpan fallbackDelay, CancellationToken cancellationToken)
    {
        Task pending;
        lock (_lock)
        {
            pending = _pending.Task;
        }

        return Task.WhenAny(pending, Task.Delay(fallbackDelay, cancellationToken))
            .WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Реализация сохраняемой очереди. Каждое изменение добавляется простой вставкой;
/// повторные записи одной задачи объединяются уже в фоновой порции без запроса
/// на проверку существования в пользовательском HTTP-запросе.
/// </summary>
public sealed class ExternalCalendarTaskSyncOutbox(
    PaymentContext db,
    ExternalCalendarTaskSyncWakeSignal wakeSignal) : IExternalCalendarTaskSyncOutbox
{
    public async Task EnqueueAsync(
        int taskId,
        string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        db.ExternalCalendarTaskSyncRequests.Add(new ExternalCalendarTaskSyncRequest
        {
            TaskJournalItemId = taskId,
            SourceProvider = NormalizeSourceProvider(sourceProvider),
            Status = ExternalCalendarTaskSyncRequestStatuses.Queued,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        wakeSignal.Pulse();
    }

    private static string NormalizeSourceProvider(string? sourceProvider)
        => string.IsNullOrWhiteSpace(sourceProvider)
            ? ExternalCalendarSyncCoordinator.CrmProvider
            : sourceProvider.Trim().ToLowerInvariant();
}

/// <summary>
/// Обрабатывает одну ограниченную порцию поручений. Повторные изменения одной
/// задачи объединяются, но запись, появившаяся во время сетевого вызова,
/// остаётся в очереди и будет обработана следующим проходом.
/// </summary>
public interface IExternalCalendarTaskSyncOutboxProcessor
{
    /// <summary>
    /// Обрабатывает одну ограниченную порцию и возвращает число объединённых групп.
    /// </summary>
    Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);
}

public sealed class ExternalCalendarTaskSyncOutboxProcessor(
    PaymentContext db,
    IExternalCalendarSyncCoordinator coordinator,
    IConfiguration configuration,
    ILogger<ExternalCalendarTaskSyncOutboxProcessor> logger)
    : IExternalCalendarTaskSyncOutboxProcessor
{
    internal const int DefaultBatchSize = 100;
    internal const int MaxAttempts = 5;

    /// <inheritdoc />
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var batchSize = Math.Clamp(
            configuration.GetValue("ExternalCalendarTaskSyncOutbox:BatchSize", DefaultBatchSize),
            1,
            500);
        var rows = await db.ExternalCalendarTaskSyncRequests
            .Where(item =>
                item.Status == ExternalCalendarTaskSyncRequestStatuses.Queued
                && item.NextAttemptAt <= now)
            .OrderBy(item => item.Id)
            .Take(batchSize * 5)
            .ToListAsync(cancellationToken);
        var groups = rows
            .GroupBy(item => new { item.TaskJournalItemId, item.SourceProvider })
            .Take(batchSize)
            .ToArray();

        foreach (var group in groups)
        {
            var newest = group.MaxBy(item => item.Id)!;
            try
            {
                await coordinator.SyncTaskForConnectedUsersAsync(
                    newest.TaskJournalItemId,
                    newest.SourceProvider,
                    cancellationToken);
                db.ExternalCalendarTaskSyncRequests.RemoveRange(group);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ExternalCalendarTaskSyncRequests.RemoveRange(group.Where(item => item.Id != newest.Id));
                newest.AttemptCount++;
                newest.UpdatedAt = DateTime.UtcNow;
                newest.LastError = LimitError(ex.Message);
                if (newest.AttemptCount >= MaxAttempts)
                {
                    newest.Status = ExternalCalendarTaskSyncRequestStatuses.Failed;
                    logger.LogError(
                        ex,
                        "External calendar task sync permanently failed for task {TaskId} after {AttemptCount} attempts.",
                        newest.TaskJournalItemId,
                        newest.AttemptCount);
                }
                else
                {
                    newest.NextAttemptAt = DateTime.UtcNow.Add(ResolveRetryDelay(newest.AttemptCount));
                    logger.LogWarning(
                        ex,
                        "External calendar task sync failed for task {TaskId}; attempt {AttemptCount} will be retried.",
                        newest.TaskJournalItemId,
                        newest.AttemptCount);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return groups.Length;
    }

    private static TimeSpan ResolveRetryDelay(int attemptCount)
        => TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, attemptCount - 1))));

    private static string LimitError(string message)
        => message.Length <= 2000 ? message : message[..2000];
}

/// <summary>
/// Постоянно обслуживает очередь синхронизации. При отсутствии работы процесс
/// спит до сигнала либо до контрольной проверки, не создавая активного polling.
/// </summary>
public sealed class ExternalCalendarTaskSyncOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ExternalCalendarTaskSyncWakeSignal wakeSignal,
    ILogger<ExternalCalendarTaskSyncOutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processed = await scope.ServiceProvider
                    .GetRequiredService<IExternalCalendarTaskSyncOutboxProcessor>()
                    .ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await wakeSignal.WaitAsync(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "External calendar task sync outbox worker failed.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}
