using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Retries in-app creation after a chat message was committed but notification dispatch failed.</summary>
public sealed class InternalChatNotificationRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<InternalChatNotificationRecoveryService> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Internal chat notification recovery tick failed.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        int[] ids;
        var retryBefore = DateTime.UtcNow.AddMinutes(-5);
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            ids = await db.InternalChatMessages.AsNoTracking()
                .Where(item => item.NotificationDispatchPending
                    && (item.LastNotificationDispatchAttemptAt == null || item.LastNotificationDispatchAttemptAt <= retryBefore))
                .OrderBy(item => item.LastNotificationDispatchAttemptAt)
                .ThenBy(item => item.Id)
                .Select(item => item.Id)
                .Take(BatchSize)
                .ToArrayAsync(ct);
        }
        var completed = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                var claimed = await db.InternalChatMessages
                    .Where(item => item.Id == id && item.NotificationDispatchPending
                        && (item.LastNotificationDispatchAttemptAt == null || item.LastNotificationDispatchAttemptAt <= retryBefore))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastNotificationDispatchAttemptAt, DateTime.UtcNow), ct);
                if (claimed == 0) continue;
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
                if (await DispatchMessageAsync(db, notifications, id, ct)) completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not deliver notifications for chat message {MessageId}; it remains queued.", id);
            }
        }
        return completed;
    }

    public static async Task<bool> DispatchMessageAsync(PaymentContext db, INotificationService notifications,
        int messageId, CancellationToken ct)
    {
        var message = await db.InternalChatMessages
            .Include(item => item.Thread)
            .Include(item => item.ReplyToMessage)
            .Include(item => item.Mentions)
            .FirstOrDefaultAsync(item => item.Id == messageId && item.NotificationDispatchPending, ct);
        if (message is null) return false;
        if (message.DeletedAt is null && message.Kind == InternalChatMessageKinds.User)
        {
            var members = await db.InternalChatMembers.AsNoTracking()
                .Where(member => member.ThreadId == message.ThreadId && member.IsActive
                    && member.JoinedAt <= message.CreatedAt && member.UserId != message.AuthorUserId
                    && (member.Thread.Kind != InternalChatThreadKinds.Task || member.Role == "guest"
                        || member.Thread.TaskJournalItem!.AuthorUserId == member.UserId
                        || member.Thread.TaskJournalItem.AssigneeUserId == member.UserId
                        || member.Thread.TaskJournalItem.Participants.Any(participant => participant.UserId == member.UserId)))
                .Select(member => new { member.UserId, member.NotificationMode })
                .ToListAsync(ct);
            var mentionIds = message.Mentions.Select(item => item.UserId).ToHashSet();
            foreach (var member in members)
            {
                var isMention = mentionIds.Contains(member.UserId);
                var isReply = message.ReplyToMessage?.AuthorUserId == member.UserId;
                if (member.NotificationMode == InternalChatNotificationModes.Muted) continue;
                if (member.NotificationMode == InternalChatNotificationModes.Mentions && !isMention && !isReply) continue;
                var type = isMention ? UserNotificationTypes.ChatMention : isReply ? UserNotificationTypes.ChatReply : UserNotificationTypes.ChatMessage;
                var title = isMention ? $"Вас упомянули: {message.Thread.Name}"
                    : isReply ? $"Ответ в чате: {message.Thread.Name}" : $"Новое сообщение: {message.Thread.Name}";
                await notifications.NotifyAsync(new NotificationEventEnvelope(
                    member.UserId,
                    type,
                    $"chat:{message.Id}:{member.UserId}:{type}",
                    title,
                    string.IsNullOrWhiteSpace(message.Text) ? "Вложение" : message.Text[..Math.Min(message.Text.Length, 180)],
                    "internalChat",
                    message.ThreadId,
                    message.Thread.TaskJournalItemId,
                    Metadata: new { threadId = message.ThreadId, messageId = message.Id },
                    Channels: null,
                    ActionUrl: $"/?ppTab=messenger&thread={message.ThreadId}"), ct);
            }
        }
        message.NotificationDispatchPending = false;
        message.LastNotificationDispatchAttemptAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
