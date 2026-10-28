using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class StickyNoteReminderNotificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StickyNoteReminderNotificationService> _logger;

    public StickyNoteReminderNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<StickyNoteReminderNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsEnabled())
            {
                try
                {
                    await ProcessDueRemindersAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Sticky note reminder processing failed.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ResolvePollSeconds()), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
        => await ProcessDueRemindersAsync(cancellationToken);

    private bool IsEnabled()
        => _configuration.GetValue("Notifications:StickyNoteReminders:Enabled", true);

    private int ResolvePollSeconds()
        => Math.Clamp(_configuration.GetValue("Notifications:StickyNoteReminders:PollSeconds", 60), 15, 3600);

    private int ResolveBatchSize()
        => Math.Clamp(_configuration.GetValue("Notifications:StickyNoteReminders:BatchSize", 100), 1, 500);

    private async Task<int> ProcessDueRemindersAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = DateTime.UtcNow;
        var dueReminders = await db.StickyNotes
            .Where(note =>
                note.AssignedUserId.HasValue
                && note.ReminderAt.HasValue
                && note.ReminderAt <= now
                && note.ReminderSentAt == null
                && note.Board != null
                && !note.Board.IsArchived)
            .OrderBy(note => note.ReminderAt)
            .ThenBy(note => note.Id)
            .Take(ResolveBatchSize())
            .Select(note => new
            {
                Note = note,
                Board = note.Board!,
                CanAssignedUserView = note.AssignedUser != null
                    && note.AssignedUser.IsActive
                    && note.AssignedUser.IsApproved
                    && note.AssignedUser.Role != null
                    && (note.AssignedUser.Role.Name.ToLower() == "admin"
                        || note.AssignedUser.Role.Permissions.Any(permission =>
                            permission.Section == RolePermissionCatalog.Notes
                            && permission.CanView))
                    && (note.Board!.Visibility == StickyNoteBoardVisibility.Shared
                        || note.Board.OwnerUserId == note.AssignedUserId
                        || note.Board.Members.Any(member => member.UserId == note.AssignedUserId))
            })
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var reminder in dueReminders)
        {
            var note = reminder.Note;
            if (!reminder.CanAssignedUserView)
            {
                note.ReminderSentAt = now;
                continue;
            }

            await notificationService.NotifyStickyNoteReminderAsync(note, cancellationToken);
            note.ReminderSentAt = now;
            sent++;
        }

        if (dueReminders.Count > 0)
        {
            // The notification has already been created with its stable event key. If a user
            // edits a note meanwhile, retain the delivery marker only for the same reminder.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateConcurrencyException error) when (attempt < 2 && error.Entries.All(entry => entry.Entity is StickyNote))
                {
                    foreach (var entry in error.Entries)
                    {
                        var originalUser = entry.OriginalValues.GetValue<int?>(nameof(StickyNote.AssignedUserId));
                        var originalReminder = entry.OriginalValues.GetValue<DateTime?>(nameof(StickyNote.ReminderAt));
                        var current = await entry.GetDatabaseValuesAsync(cancellationToken);
                        if (current is null || current.GetValue<bool>("IsDeleted")
                            || current.GetValue<int?>(nameof(StickyNote.AssignedUserId)) != originalUser
                            || current.GetValue<DateTime?>(nameof(StickyNote.ReminderAt)) != originalReminder
                            || current.GetValue<DateTime?>(nameof(StickyNote.ReminderSentAt)).HasValue)
                        {
                            entry.State = EntityState.Detached;
                            continue;
                        }
                        entry.CurrentValues.SetValues(current);
                        entry.OriginalValues.SetValues(current);
                        entry.State = EntityState.Unchanged;
                        entry.Property(nameof(StickyNote.ReminderSentAt)).CurrentValue = now;
                    }
                }
            }
        }

        return sent;
    }
}
