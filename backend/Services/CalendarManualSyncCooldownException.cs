namespace PayPlanner.Api.Services;

public sealed class CalendarManualSyncCooldownException : InvalidOperationException
{
    public CalendarManualSyncCooldownException(string message, DateTime availableAt)
        : base(message)
    {
        AvailableAt = availableAt;
    }

    public DateTime AvailableAt { get; }
}
