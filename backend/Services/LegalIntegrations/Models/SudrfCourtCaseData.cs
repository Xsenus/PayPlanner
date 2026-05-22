namespace PayPlanner.Api.Services.LegalIntegrations.Models;

public sealed class SudrfCourtCaseData
{
    public string SourceUrl { get; init; } = string.Empty;
    public string CourtName { get; init; } = string.Empty;
    public string CaseNumber { get; init; } = string.Empty;
    public string JudicialUid { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string JudgeName { get; init; } = string.Empty;
    public string SecretaryName { get; init; } = string.Empty;
    public string LowerCourtName { get; init; } = string.Empty;
    public string LowerCourtCaseNumber { get; init; } = string.Empty;
    public string Instance { get; init; } = "first";
    public DateTime? ReceivedAt { get; init; }
    public DateTime? NextHearingAt { get; init; }
    public string NextHearingPlace { get; init; } = string.Empty;
    public IReadOnlyList<SudrfCourtMovementEvent> MovementEvents { get; init; } = [];
    public IReadOnlyList<SudrfCourtParticipant> Participants { get; init; } = [];
    public IReadOnlyList<SudrfCourtAct> CourtActs { get; init; } = [];
}

public sealed class SudrfCourtMovementEvent
{
    public string Title { get; init; } = string.Empty;
    public DateTime? ScheduledAt { get; init; }
    public string Place { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime? PostedAt { get; init; }
    public string SourceSummary { get; init; } = string.Empty;
    public string SecretaryName { get; init; } = string.Empty;
}

public sealed class SudrfCourtParticipant
{
    public string Role { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public sealed class SudrfCourtAct
{
    public string Title { get; init; } = string.Empty;
    public DateTime? ActDate { get; init; }
    public DateTime? PostedAt { get; init; }
    public string Result { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
}
