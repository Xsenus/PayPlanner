namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class SudrfCourtSyncException : Exception
{
    public SudrfCourtSyncException(string message)
        : base(message)
    {
    }

    public SudrfCourtSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
