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
            },
        };
}

public sealed class LegalFeatureFlagsDto
{
    public bool ExternalCourtSync { get; set; }
    public bool ExternalCalendarSync { get; set; }
    public bool CourtEvents { get; set; }
    public bool DocumentTemplateLibrary { get; set; }
}
