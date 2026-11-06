using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SyntheticBrowserPhaseBudgetTests
{
    [Fact]
    public void ColdTaskPreparationHasItsOwnBoundAndDoesNotStartTheAssertionWindow()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, taskUpload: true);
        foreach (var line in new[] { "TASK_UPLOAD_STAGE_MODULES_READY", "TASK_UPLOAD_STAGE_VITE_ENTRY_READY",
            "PP_CASE_RETIREMENT_BROWSER_PHASE cli-open", "PP_FINANCIAL_BROWSER_PHASE cli-open" }) budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
    }

    [Fact]
    public void TaskBrowserReadinessRetainsTheOriginalNinetySecondAssertionWindow()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, taskUpload: true);
        budget.ObserveLine("TASK_UPLOAD_STAGE_BROWSER_READY");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90) }, deadlines);
    }

    [Fact]
    public void TaskProgressAndRepeatedReadinessCannotExtendEitherWindow()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, taskUpload: true);
        foreach (var line in new[] { "TASK_UPLOAD_STAGE_BROWSER_READY extra", " TASK_UPLOAD_STAGE_BROWSER_READY",
            "TASK_UPLOAD_STAGE_BROWSER_VERSION_1", "TASK_UPLOAD_STAGE_AUTHENTICATED" }) budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
        for (var index = 0; index < 100; index++) budget.ObserveLine("TASK_UPLOAD_STAGE_BROWSER_READY");
        foreach (var line in new[] { "TASK_UPLOAD_STAGE_AUTHENTICATED", "TASK_UPLOAD_QUEUE_READY", "TASK_UPLOAD_STAGE_QUEUE_COMPLETED" }) budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90) }, deadlines);
    }

    [Fact]
    public void TaskPreparationCannotBeCombinedWithAnotherBrowserFamily()
    {
        var deadlines = new List<TimeSpan>();
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(true, deadlines.Add, taskUpload: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, chat: true, taskUpload: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, retirement: true, taskUpload: true));
        Assert.Empty(deadlines);
    }

    [Fact]
    public void ColdRetirementPreparationHasASeparateBoundedDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, retirement: true);
        foreach (var line in new[] { "CASE_RETIREMENT_BUNDLE_PHASE production-build",
            "PP_CHAT_BROWSER_PHASE cli-open", "PP_FINANCIAL_BROWSER_PHASE cli-open" })
            budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
    }

    [Fact]
    public void RetirementBrowserStartKeepsTheOriginalThreeMinuteAssertionBudget()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, retirement: true);
        budget.ObserveLine("PP_CASE_RETIREMENT_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void RetirementProgressAndRepeatedStartCannotRenewTheBrowserDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, retirement: true);
        foreach (var line in new[] { "cli-open", " PP_CASE_RETIREMENT_BROWSER_PHASE cli-open",
            "PP_CASE_RETIREMENT_BROWSER_PHASE cli-open extra" }) budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5) }, deadlines);
        for (var index = 0; index < 100; index++) budget.ObserveLine("PP_CASE_RETIREMENT_BROWSER_PHASE cli-open");
        foreach (var phase in new[] { "browser-goto", "scenario", "browser-close", "production-build" })
            budget.ObserveLine("CASE_RETIREMENT_BUNDLE_PHASE " + phase);
        Assert.Equal(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void RetirementCannotBeCombinedWithAnotherBrowserFamily()
    {
        var deadlines = new List<TimeSpan>();
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(true, deadlines.Add, retirement: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, chat: true, retirement: true));
        Assert.Empty(deadlines);
    }

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
        Assert.Equal(new[] { TimeSpan.FromMinutes(10) }, deadlines);
    }

    [Fact]
    public void BrowserStartRetainsTheOriginalThreeMinuteAssertionBudget()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void RepeatedBrowserStartCannotExtendTheDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        for (var index = 0; index < 100; index++) budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        Assert.Equal(new[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void LaterProgressAndShutdownCannotExtendTheBrowserDeadline()
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(true, deadlines.Add);
        budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE cli-open");
        foreach (var phase in new[] { "cli-goto", "cli-snapshot", "scenario", "browser-close", "vite-close", "control-close", "stdin-close", "done", "vite-create" })
            budget.ObserveLine("PP_FINANCIAL_BROWSER_PHASE " + phase);
        Assert.Equal(new[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3) }, deadlines);
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
        Assert.Equal(new[] { TimeSpan.FromMinutes(10) }, deadlines);
    }

    [Theory]
    [InlineData("generated", "PP_GENERATED_BROWSER_PHASE cli-open")]
    [InlineData("court", "PP_COURT_BROWSER_PHASE cli-open")]
    public void DocumentPreparationIsSeparateAndCannotRenewTheOriginalAssertionDeadline(string family, string marker)
    {
        var deadlines = new List<TimeSpan>();
        var budget = new SyntheticBrowserPhaseBudget(false, deadlines.Add, generated: family == "generated", court: family == "court");
        foreach (var line in new[] { marker.Replace("cli-open", "ui-build"), " " + marker, marker + " extra",
            "PP_FINANCIAL_BROWSER_PHASE cli-open", "PP_CHAT_BROWSER_PHASE cli-open" }) budget.ObserveLine(line);
        Assert.Equal(new[] { TimeSpan.FromMinutes(10) }, deadlines);
        for (var index = 0; index < 100; index++) budget.ObserveLine(marker);
        budget.ObserveLine(marker.Replace("cli-open", "ui-build"));
        Assert.Equal(new[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3) }, deadlines);
    }

    [Fact]
    public void GeneratedAndCourtFamiliesCannotMixWithOtherBrowserFamilies()
    {
        var deadlines = new List<TimeSpan>();
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, generated: true, court: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(true, deadlines.Add, generated: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, court: true, retirement: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, chat: true, generated: true));
        Assert.Throws<ArgumentException>(() => new SyntheticBrowserPhaseBudget(false, deadlines.Add, taskUpload: true, court: true));
        Assert.Empty(deadlines);
    }
}
