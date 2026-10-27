using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models.Responses;

public sealed record PaymentCreateAttemptCancelOutcomeDto(
    Guid AttemptId, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? CanonicalPaymentId);
