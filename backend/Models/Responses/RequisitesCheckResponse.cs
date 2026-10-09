namespace PayPlanner.Api.Models.Responses;

public sealed record RequisitesIssue(string Field, string Message);

public sealed record RequisitesCheckResponse(int LegalEntityId, IReadOnlyList<RequisitesIssue> Issues);
