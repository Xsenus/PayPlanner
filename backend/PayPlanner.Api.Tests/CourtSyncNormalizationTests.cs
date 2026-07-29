using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncNormalizationTests
{
    [Theory]
    [InlineData("first", "first")]
    [InlineData(" APPEAL ", "appeal")]
    [InlineData("cassation", "cassation")]
    [InlineData("supervision", "supreme")]
    [InlineData("unknown", "first")]
    public void NormalizeInstance_ReturnsCanonicalCode(string source, string expected)
        => Assert.Equal(expected, CourtSyncNormalization.NormalizeInstance(source));

    [Theory]
    [InlineData("ОТВЕТЧИК", "respondent")]
    [InlineData("Должник", "respondent")]
    [InlineData("Заинтересованное лицо", "thirdParty")]
    [InlineData("Заявитель", "applicant")]
    [InlineData("Административный истец", "claimant")]
    [InlineData("Взыскатель", "claimant")]
    [InlineData("Представитель", "other")]
    public void ResolvePartySideFromRole_ReturnsCanonicalSide(string role, string expected)
        => Assert.Equal(expected, CourtSyncNormalization.ResolvePartySideFromRole(role));

    [Theory]
    [InlineData("", "scheduled")]
    [InlineData("Судебное заседание отложено", "postponed")]
    [InlineData("Заседание снято с рассмотрения", "cancelled")]
    [InlineData("Дело рассмотрено", "completed")]
    [InlineData("Заседание состоялось", "held")]
    public void ResolveMovementStatusFromResult_ReturnsCanonicalStatus(string result, string expected)
        => Assert.Equal(expected, CourtSyncNormalization.ResolveMovementStatusFromResult(result));

    [Fact]
    public void Labels_AreResolvedFromCanonicalValues()
    {
        Assert.Equal("Апелляция", CourtSyncNormalization.GetInstanceLabel("appeal"));
        Assert.Equal("Судебное решение", CourtSyncNormalization.GetEventTypeLabel("decision"));
        Assert.Equal("Отменено", CourtSyncNormalization.GetMovementStatusLabel("cancelled"));
        Assert.Equal("В архиве", CourtSyncNormalization.GetMovementStatusLabel("archived"));
        Assert.Equal("Третье лицо", CourtSyncNormalization.GetPartySideLabel("thirdParty"));
    }
}
