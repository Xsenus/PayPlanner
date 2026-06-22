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
        var dueNotes = await db.StickyNotes
            .Include(note => note.Board)
            .ThenInclude(board => board!.Members)
            .Include(note => note.AssignedUser)
            .ThenInclude(user => user!.Role)
            .ThenInclude(role => role!.Permissions)
            .AsSplitQuery()
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
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var note in dueNotes)
        {
            if (note.AssignedUser is null || !CanAssignedUserViewNote(note))
            {
                note.ReminderSentAt = now;
                continue;
            }

            await notificationService.NotifyStickyNoteReminderAsync(note, cancellationToken);
            note.ReminderSentAt = now;
            sent++;
        }

        if (dueNotes.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }

    private static bool CanAssignedUserViewNote(StickyNote note)
    {
        if (!note.AssignedUserId.HasValue || note.Board is null || note.AssignedUser is null)
        {
            return false;
        }

        var user = note.AssignedUser;
        if (!user.IsActive || !user.IsApproved)
        {
            return false;
        }

        var canViewNotes = string.Equals(user.Role?.Name, "admin", StringComparison.OrdinalIgnoreCase)
            || user.Role?.Permissions.Any(permission =>
                permission.Section == RolePermissionCatalog.Notes
                && permission.CanView) == true;
        if (!canViewNotes)
        {
            return false;
        }

        return note.Board.Visibility == StickyNoteBoardVisibility.Shared
            || note.Board.OwnerUserId == user.Id
            || note.Board.Members.Any(member => member.UserId == user.Id);
    }
}
