namespace PayPlanner.Api.Models.Requests;

public sealed class UpsertPushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public PushSubscriptionKeysRequest Keys { get; set; } = new();
    public string? Browser { get; set; }
    public string? Platform { get; set; }
    public string? DeviceName { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class PushSubscriptionKeysRequest
{
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public sealed class RevokePushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
}
