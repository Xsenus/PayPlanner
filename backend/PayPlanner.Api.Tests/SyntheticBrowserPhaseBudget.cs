namespace PayPlanner.Api.Tests;

/// <summary>Cold production compilation has its own bound; financial browser assertions retain their original three minutes.</summary>
internal sealed class SyntheticBrowserPhaseBudget
{
    private readonly bool financial;
    private readonly Action<TimeSpan> reschedule;
    private bool browserStarted;

    internal SyntheticBrowserPhaseBudget(bool financial, Action<TimeSpan> reschedule)
    {
        this.financial = financial;
        this.reschedule = reschedule;
        reschedule(TimeSpan.FromMinutes(financial ? 5 : 3));
    }

    internal void ObserveLine(string line)
    {
        // One transition only: repeated child output cannot extend the deadline.
        if (!financial || browserStarted || line != "PP_FINANCIAL_BROWSER_PHASE cli-open") return;
        browserStarted = true;
        reschedule(TimeSpan.FromMinutes(3));
    }
}
