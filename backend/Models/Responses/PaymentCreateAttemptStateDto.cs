using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models.Responses;

public sealed record PaymentCreateAttemptStateDto(
    Guid AttemptId, string State, string Operation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SellerCompanyId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SellerBranchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PrimaryPaymentId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? CanonicalPaymentId,
    string IdentityVersion);
