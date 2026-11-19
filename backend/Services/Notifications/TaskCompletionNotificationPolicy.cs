namespace PayPlanner.Api.Services.Notifications;

public static class TaskCompletionNotificationPolicy
{
    public const string OrdinaryEventType = "ordinary-completion";
    public const string CoalescedEventType = "ordinary-coalesced";
    public const string ApprovalEventType = "approval-completion";
    public const string OrdinaryOrigin = "ordinary";
    public const string ApprovalOrigin = "approval";
    public const string LegacyOrigin = "legacy";
    public static readonly string[] OrdinaryEventTypes = [OrdinaryEventType, CoalescedEventType];

    public static string ApprovalDecisionEventKey(string completionEventKey)
        => $"{completionEventKey}:approval-decision";

    public static string OriginForEventType(string eventType)
        => eventType switch
        {
            OrdinaryEventType or CoalescedEventType => OrdinaryOrigin,
            ApprovalEventType => ApprovalOrigin,
            _ => LegacyOrigin
        };

    public static IReadOnlySet<string> ResolveEnabledChannels(
        UserNotificationPreferenceSnapshot preferences,
        string type,
        UserNotificationDoNotDisturbSnapshot quietHours,
        TimeZoneInfo timeZone,
        DateTime nowUtc,
        bool requireInAppForDelivery = false)
    {
        var definition = UserNotificationTypes.Find(type);
        var channels = (definition?.PreferenceChannels ?? [])
            .Where(channel => UserNotificationPreferenceSettings.IsChannelEnabled(preferences, type, channel))
            .Where(channel => !UserNotificationDoNotDisturbSettings.IsChannelMuted(quietHours, definition, channel, nowUtc, timeZone))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requireInAppForDelivery && !channels.Contains(NotificationChannels.InApp))
        {
            channels.Remove(NotificationChannels.Max);
            channels.Remove(NotificationChannels.WebPush);
        }

        return channels;
    }

    public static string? ResolveChannelPreferenceType(
        UserNotificationPreferenceSnapshot preferences,
        string notificationType,
        string channel,
        bool coalesced)
    {
        var primary = UserNotificationTypes.Find(notificationType);
        if (primary?.PreferenceChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) == true
            && UserNotificationPreferenceSettings.IsChannelEnabled(preferences, primary.Key, channel))
            return primary.Key;

        var fallback = UserNotificationTypes.Find(UserNotificationTypes.TaskChanged)!;
        if (coalesced && notificationType == UserNotificationTypes.TaskCompleted
            && !string.Equals(channel, NotificationChannels.ForcedModal, StringComparison.OrdinalIgnoreCase)
            && fallback.PreferenceChannels.Contains(channel, StringComparer.OrdinalIgnoreCase)
            && UserNotificationPreferenceSettings.IsChannelEnabled(preferences, fallback.Key, channel))
            return fallback.Key;

        return null;
    }

    public static bool IsForcedModalEnabled(
        string type,
        bool ordinaryCompletion,
        bool inAppEnabled,
        bool forcedModalEnabled)
        => forcedModalEnabled
            && (type != UserNotificationTypes.TaskCompleted
                || (inAppEnabled && ordinaryCompletion));

    public static (bool ForcedModalEnabled, bool ToastEnabled) ResolvePresentation(
        string type, bool ordinaryCompletion, bool inAppEnabled, bool forcedModalEnabled, bool toastEnabled)
    {
        var showModal = IsForcedModalEnabled(type, ordinaryCompletion, inAppEnabled, forcedModalEnabled);
        return (showModal, toastEnabled && (type != UserNotificationTypes.TaskCompleted || (inAppEnabled && !showModal)));
    }

    public static (bool ForcedModalEnabled, bool ToastEnabled) ResolvePresentation(
        string type,
        bool ordinaryCompletion,
        IReadOnlySet<string> primary,
        bool coalesced = false,
        IReadOnlySet<string>? fallback = null)
    {
        var primaryInApp = primary.Contains(NotificationChannels.InApp);
        var showModal = IsForcedModalEnabled(type, ordinaryCompletion, primaryInApp, primary.Contains(NotificationChannels.ForcedModal));
        var toast = primary.Contains(NotificationChannels.Toast);
        if (type == UserNotificationTypes.TaskCompleted)
        {
            toast = (primaryInApp && toast)
                || (coalesced && fallback?.Contains(NotificationChannels.InApp) == true && fallback.Contains(NotificationChannels.Toast));
            return (showModal, toast && !showModal);
        }

        return (showModal, toast);
    }
}
