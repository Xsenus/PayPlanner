namespace PayPlanner.Api.Tests;

/// <summary>Cold preparation has its own bound; each browser family retains its original assertion deadline.</summary>
internal sealed class SyntheticBrowserPhaseBudget
{
    private readonly string? browserStartMarker;
    private readonly Action<TimeSpan> reschedule;
    private readonly TimeSpan browserBudget;
    private bool browserStarted;

    internal SyntheticBrowserPhaseBudget(bool financial, Action<TimeSpan> reschedule, bool chat = false,
        bool retirement = false, bool taskUpload = false)
    {
        if ((financial ? 1 : 0) + (chat ? 1 : 0) + (retirement ? 1 : 0) + (taskUpload ? 1 : 0) > 1)
            throw new ArgumentException("Select only one browser family.", taskUpload ? nameof(taskUpload) : retirement ? nameof(retirement) : nameof(chat));
        browserStartMarker = financial ? "PP_FINANCIAL_BROWSER_PHASE cli-open"
            : chat ? "PP_CHAT_BROWSER_PHASE cli-open"
            : retirement ? "PP_CASE_RETIREMENT_BROWSER_PHASE cli-open"
            : taskUpload ? "TASK_UPLOAD_STAGE_BROWSER_READY" : null;
        this.reschedule = reschedule;
        browserBudget = taskUpload ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
        reschedule(TimeSpan.FromMinutes(browserStartMarker is null ? 3 : 5));
    }

    internal void ObserveLine(string line)
    {
        // One transition only: repeated child output cannot extend the deadline.
        if (browserStartMarker is null || browserStarted || line != browserStartMarker) return;
        browserStarted = true;
        reschedule(browserBudget);
    }
}
