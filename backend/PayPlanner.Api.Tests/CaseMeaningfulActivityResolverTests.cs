using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseMeaningfulActivityResolverTests
{
    private static readonly DateTime CreatedAt =
        new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("task", CaseMeaningfulActivitySources.Task)]
    [InlineData("task-comment", CaseMeaningfulActivitySources.TaskComment)]
    [InlineData("document", CaseMeaningfulActivitySources.Document)]
    [InlineData("court-event", CaseMeaningfulActivitySources.CourtEvent)]
    [InlineData("responsible-view", CaseMeaningfulActivitySources.ResponsibleView)]
    [InlineData("activity-confirmation", CaseMeaningfulActivitySources.ActivityConfirmation)]
    public void Resolve_RecognizesEveryMeaningfulSignal(string scenario, string expectedSource)
    {
        var signals = scenario switch
        {
            "task" => new CaseMeaningfulActivitySignals(CreatedAt, LastTaskAt: CreatedAt.AddHours(1)),
            "task-comment" => new CaseMeaningfulActivitySignals(CreatedAt, LastTaskCommentAt: CreatedAt.AddHours(2)),
            "document" => new CaseMeaningfulActivitySignals(CreatedAt, LastDocumentAt: CreatedAt.AddHours(3)),
            "court-event" => new CaseMeaningfulActivitySignals(CreatedAt, LastCourtEventAt: CreatedAt.AddHours(4)),
            "responsible-view" => new CaseMeaningfulActivitySignals(CreatedAt, LastResponsibleViewAt: CreatedAt.AddHours(5)),
            "activity-confirmation" => new CaseMeaningfulActivitySignals(CreatedAt, LastActivityConfirmationAt: CreatedAt.AddHours(6)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var result = CaseMeaningfulActivityResolver.Resolve(signals);

        Assert.Equal(expectedSource, result.Source);
        Assert.True(result.At > CreatedAt);
    }

    [Fact]
    public void Resolve_ReturnsNewestSignalRegardlessOfInputCategory()
    {
        var result = CaseMeaningfulActivityResolver.Resolve(
            new CaseMeaningfulActivitySignals(
                CreatedAt,
                LastTaskAt: CreatedAt.AddDays(5),
                LastTaskCommentAt: CreatedAt.AddDays(4),
                LastDocumentAt: CreatedAt.AddDays(3),
                LastCourtEventAt: CreatedAt.AddDays(2),
                LastResponsibleViewAt: CreatedAt.AddDays(1),
                LastActivityConfirmationAt: CreatedAt.AddHours(12)));

        Assert.Equal(CreatedAt.AddDays(5), result.At);
        Assert.Equal(CaseMeaningfulActivitySources.Task, result.Source);
    }

    [Fact]
    public void Resolve_DoesNotTreatMissingOrOlderSignalsAsActivity()
    {
        var result = CaseMeaningfulActivityResolver.Resolve(
            new CaseMeaningfulActivitySignals(
                CreatedAt,
                LastTaskAt: CreatedAt.AddMinutes(-1),
                LastTaskCommentAt: null,
                LastDocumentAt: CreatedAt.AddDays(-1),
                LastCourtEventAt: null,
                LastResponsibleViewAt: CreatedAt.AddHours(-1),
                LastActivityConfirmationAt: null));

        Assert.Equal(CreatedAt, result.At);
        Assert.Equal(CaseMeaningfulActivitySources.CaseCreated, result.Source);
    }
}
