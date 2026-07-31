namespace PayPlanner.Api.Models.Responses;

public sealed class AppFeatureFlagsDto
{
    public LegalFeatureFlagsDto Legal { get; set; } = new();

    public static AppFeatureFlagsDto FromOptions(AppFeatureFlagsOptions options)
        => new()
        {
            Legal = new LegalFeatureFlagsDto
            {
                ExternalCourtSync = options.Legal.ExternalCourtSync,
                ExternalCalendarSync = options.Legal.ExternalCalendarSync,
                CourtEvents = options.Legal.CourtEvents,
                DocumentTemplateLibrary = options.Legal.DocumentTemplateLibrary,
                ExternalWorkOrderApproval = options.Legal.ExternalWorkOrderApproval,
            },
        };
}

public sealed class LegalFeatureFlagsDto
{
    public bool ExternalCourtSync { get; set; }
    public bool ExternalCalendarSync { get; set; }
    public bool CourtEvents { get; set; }
    public bool DocumentTemplateLibrary { get; set; }
    public bool ExternalWorkOrderApproval { get; set; }
}

public sealed class LegalIntegrationDiagnosticsDto
{
    public DateTime GeneratedAt { get; set; }
    public bool HasEnabledExternalLegalFlags { get; set; }
    public IReadOnlyList<LegalIntegrationDiagnosticItemDto> Items { get; set; } = [];
}

public sealed class LegalIntegrationDiagnosticItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FeatureFlagPath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool SafeToRun { get; set; }
    public string Message { get; set; } = string.Empty;
}
