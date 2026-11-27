namespace PayPlanner.Api.Tests;

/// <summary>Cold preparation has its own bound; each browser family retains its original assertion deadline.</summary>
internal sealed class SyntheticBrowserPhaseBudget
{
    private const string TaskBrowserReady = "TASK_UPLOAD_STAGE_BROWSER_READY";
    private readonly string? browserStartMarker;
    private readonly Action<TimeSpan> reschedule;
    private readonly TimeSpan browserBudget;
    private bool browserStarted;
    private bool taskBrowserLaunching;

    internal SyntheticBrowserPhaseBudget(bool financial, Action<TimeSpan> reschedule, bool chat = false,
        bool retirement = false, bool taskUpload = false, bool generated = false, bool court = false)
    {
        if ((financial ? 1 : 0) + (chat ? 1 : 0) + (retirement ? 1 : 0) + (taskUpload ? 1 : 0)
            + (generated ? 1 : 0) + (court ? 1 : 0) > 1)
            throw new ArgumentException("Select only one browser family.", taskUpload ? nameof(taskUpload) : retirement ? nameof(retirement)
                : generated ? nameof(generated) : court ? nameof(court) : nameof(chat));
        browserStartMarker = financial ? "PP_FINANCIAL_BROWSER_PHASE cli-open"
            : chat ? "PP_CHAT_BROWSER_PHASE cli-open"
            : retirement ? "PP_CASE_RETIREMENT_BROWSER_PHASE cli-open"
            : taskUpload ? TaskBrowserReady
            : generated ? "PP_GENERATED_BROWSER_PHASE cli-open"
            : court ? "PP_COURT_BROWSER_PHASE cli-open" : null;
        this.reschedule = reschedule;
        browserBudget = taskUpload ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
        // Cold Windows dependency/input preparation consumed nearly five
        // minutes before the artifact lock. Keep that admission separate from
        // the unchanged three-minute document assertion window.
        reschedule(TimeSpan.FromMinutes(browserStartMarker is null ? 3 : financial || generated || court ? 10 : 5));
    }

    internal void ObserveLine(string line)
    {
        if (browserStartMarker is null || browserStarted) return;
        if (browserStartMarker == TaskBrowserReady && !taskBrowserLaunching && line == "TASK_UPLOAD_STAGE_VITE_ENTRY_READY")
        {
            // A cold build can exhaust almost the whole preparation budget before Chromium is launched.
            taskBrowserLaunching = true;
            reschedule(TimeSpan.FromSeconds(90));
            return;
        }
        // Repeated stage output cannot extend the launch or assertion deadline.
        if (line != browserStartMarker) return;
        browserStarted = true;
        reschedule(browserBudget);
    }
}
