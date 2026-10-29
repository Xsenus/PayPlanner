using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SyntheticBrowserPhaseBudgetTests
{
    [Fact]
    public void ColdChatPreparationAndBrowserStartHaveSeparateBoundedDeadlines()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, chat: true);
        foreach (var phase in new[] { "ui-build", "preview-listen", "pending-scenario" })
            budget.ObserveLine("PP_CHAT_BROWSER_PHASE " + phase);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
        for (var index = 0; index < 100; index++) budget.ObserveLine("PP_CHAT_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void ChatProgressAndUnrecognisedOutputCannotExtendEitherDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, chat: true);
        foreach (var line in new[] { "cli-open", " PP_CHAT_BROWSER_PHASE cli-open", "PP_CHAT_BROWSER_PHASE cli-open extra" })
            budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
        budget.ObserveLine("PP_CHAT_BROWSER_PHASE cli-open");
        foreach (var phase in new[] { "ui-build", "preview-listen", "pending-scenario", "apply-boundary", "published-scenario", "browser-close" })
            budget.ObserveLine("PP_CHAT_BROWSER_PHASE " + phase);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void ChatOutputCannotExtendOtherBrowserFamilies()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add);
        budget.ObserveLine("PP_CHAT_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void AmbiguousFamilyCannotScheduleAnyDeadline()
    {
        var deadlines = new List<TimeSpan>();
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(true, deadlines.Add, chat: true));
        Assert.Empty(deadlines);
    }

    [Fact]
    public void ColdFinancialPreparationRemainsBoundedUntilTheBrowserStarts()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        foreach (var phase in new[] { "vite-create", "vite-listen", "entry-transform", "requests-idle" })
            budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE " + phase);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
    }

    [Fact]
    public void BrowserStartRetainsTheOriginalThreeMinuteAssertionBudget()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void RepeatedBrowserStartCannotExtendTheDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        for (var index = 0; index < 100; index++) budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void LaterProgressAndShutdownCannotExtendTheBrowserDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        foreach (var phase in new[] { "cli-goto", "cli-snapshot", "scenario", "browser-close", "vite-close", "control-close", "stdin-close", "done", "vite-create" })
            budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE " + phase);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void OtherBrowserFamiliesKeepTheirExistingTotalDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        budget.ObserveLine("CASE_RETIREMENT_BUNDLE_PHASE browser-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void UnrecognisedOutputCannotStartOrRenewTheBrowserBudget()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        foreach (var line in new[] { "cli-open", " PP_FINANCIAL_BROWSER_PHASE cli-open", "PP_FINANCIAL_BROWSER_PHASE cli-open extra", "PP_FINANCIAL_BROWSER_PHASE cli-opened", "PP_CHAT_BROWSER_PHASE cli-open" })
            budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
    }
}
