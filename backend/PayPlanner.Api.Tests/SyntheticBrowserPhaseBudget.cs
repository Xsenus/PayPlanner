namespace PayPlanner.Api.Tests;

/// <summary>Cold production preparation has its own bound; browser assertions retain their original three minutes.</summary>
internal sealed class SyntheticBrowserPhaseBudget
{
    private readonly string? browserStartMarker;
    private readonly Action<TimeSpan> reschedule;
    private bool browserStarted;

    internal SyntheticBrowserPhaseBudget(bool financial, Action<TimeSpan> reschedule, bool chat = false,
        bool retirement = false)
    {
        if ((financial ? 1 : 0) + (chat ? 1 : 0) + (retirement ? 1 : 0) > 1)
            throw new ArgumentException("Select only one browser family.", retirement ? nameof(retirement) : nameof(chat));
        browserStartMarker = financial ? "PP_FINANCIAL_BROWSER_PHASE cli-open"
            : chat ? "PP_CHAT_BROWSER_PHASE cli-open"
            : retirement ? "PP_CASE_RETIREMENT_BROWSER_PHASE cli-open" : null;
        this.reschedule = reschedule;
        reschedule(TimeSpan.FromMinutes(browserStartMarker is null ? 3 : 5));
    }

    internal void ObserveLine(string line)
    {
        // One transition only: repeated child output cannot extend the deadline.
        if (browserStartMarker is null || browserStarted || line != browserStartMarker) return;
        browserStarted = true;
        reschedule(TimeSpan.FromMinutes(3));
    }
}
