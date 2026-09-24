using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class InternalChatNoReplyReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InternalChatNoReplyReminderService> _logger;

    public InternalChatNoReplyReminderService(
        IServiceScopeFactory scopeFactory,
        ILogger<InternalChatNoReplyReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Internal chat no-reply reminder tick failed.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = DateTime.UtcNow;
        var due = await db.InternalChatMessages
            .Where(message => message.NextNoReplyReminderAt != null
                && message.NextNoReplyReminderAt <= now
                && message.DeletedAt == null
                && message.Kind == InternalChatMessageKinds.User)
            .OrderBy(message => message.NextNoReplyReminderAt)
            .ThenBy(message => message.Id)
            .Take(100)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        var messageIds = due.Select(message => message.Id).ToArray();
        var threadIds = due.Select(message => message.ThreadId).Distinct().ToArray();
        var answeredIds = (await db.InternalChatMessages.AsNoTracking()
            .Where(reply => reply.ReplyToMessageId.HasValue && messageIds.Contains(reply.ReplyToMessageId.Value)
                && reply.DeletedAt == null && reply.AuthorUserId != reply.ReplyToMessage!.AuthorUserId)
            .Select(reply => reply.ReplyToMessageId!.Value)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
        var authorizedMembers = (await db.InternalChatMembers.AsNoTracking()
            .Where(item => threadIds.Contains(item.ThreadId) && item.IsActive && item.User.IsActive
                && !item.Thread.IsArchived
                && (item.Thread.Kind != InternalChatThreadKinds.Task || item.Role == "guest"
                    || item.Thread.TaskJournalItem!.AuthorUserId == item.UserId
                    || item.Thread.TaskJournalItem.AssigneeUserId == item.UserId
                    || item.Thread.TaskJournalItem.Participants.Any(participant => participant.UserId == item.UserId)))
            .Select(item => new { item.ThreadId, item.UserId })
            .ToListAsync(ct)).Select(item => (item.ThreadId, item.UserId)).ToHashSet();
        var threads = await db.InternalChatThreads.AsNoTracking()
            .Where(item => threadIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name, item.TaskJournalItemId })
            .ToDictionaryAsync(item => item.Id, ct);
        var sent = 0;

        foreach (var message in due)
        {
            if (message.AuthorUserId is not int authorId
                || answeredIds.Contains(message.Id)
                || !authorizedMembers.Contains((message.ThreadId, authorId))
                || !threads.TryGetValue(message.ThreadId, out var thread))
            {
                message.NextNoReplyReminderAt = null;
                await db.SaveChangesAsync(ct);
                continue;
            }

            var dueAt = message.NextNoReplyReminderAt!.Value;
            var delivered = await notifications.NotifyAsync(new NotificationEventEnvelope(
                authorId,
                UserNotificationTypes.ChatNoReplyReminder,
                $"chat:{message.Id}:no-reply:{dueAt:yyyyMMddHHmm}",
                $"Нет ответа в чате: {thread.Name}",
                "На ваше сообщение пока не ответили. Откройте чат или отключите напоминание у сообщения.",
                "internalChat",
                message.ThreadId,
                thread.TaskJournalItemId,
                Metadata: new { threadId = message.ThreadId, messageId = message.Id },
                ActionUrl: $"/?ppTab=messenger&thread={message.ThreadId}"), ct);
            message.NextNoReplyReminderAt = now.AddDays(1);
            await db.SaveChangesAsync(ct);
            if (delivered) sent++;
        }

        return sent;
    }
}
