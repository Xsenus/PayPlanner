namespace PayPlanner.Api.Models;

public sealed class AppFeatureFlagsOptions
{
    public const string SectionName = "FeatureFlags";

    public LegalFeatureFlagsOptions Legal { get; set; } = new();
}

public sealed class LegalFeatureFlagsOptions
{
    public bool ExternalCourtSync { get; set; }
    public bool ExternalCalendarSync { get; set; }
    public bool CourtEvents { get; set; }
    public bool DocumentTemplateLibrary { get; set; }
}
