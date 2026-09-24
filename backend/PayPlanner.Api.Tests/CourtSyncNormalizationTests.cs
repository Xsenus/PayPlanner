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
    [InlineData("Назначено судебное заседание", "scheduled")]
    [InlineData("Ожидает рассмотрения", "scheduled")]
    [InlineData("Судебное заседание отложено", "postponed")]
    [InlineData("Заседание снято с рассмотрения", "cancelled")]
    [InlineData("Дело рассмотрено", "completed")]
    [InlineData("Заседание состоялось", "held")]
    public void ResolveMovementStatusFromResult_ReturnsCanonicalStatus(string result, string expected)
        => Assert.Equal(expected, CourtSyncNormalization.ResolveMovementStatusFromResult(result));

    [Theory]
    [InlineData("Вынесено решение", "", "", true)]
    [InlineData("Изготовлено мотивированное решение", "", "", true)]
    [InlineData("Решение суда", "Иск удовлетворен частично", "", true)]
    [InlineData("Решение суда", "Требования удовлетворены частично", "", true)]
    [InlineData("Решение суда", "В удовлетворении иска отказано", "", true)]
    [InlineData("Решение суда", "", "", false)]
    [InlineData("Назначено вынесение решения суда", "", "", false)]
    [InlineData("Решение суда", "Ожидается решение", "", false)]
    [InlineData("Вынесено решение", "Назначено оглашение", "", false)]
    public void IsConfirmedDecisionMovement_RequiresCompletedOutcome(
        string title,
        string result,
        string reason,
        bool expected)
        => Assert.Equal(expected, CourtSyncNormalization.IsConfirmedDecisionMovement(title, result, reason));

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
