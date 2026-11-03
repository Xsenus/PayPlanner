using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.Json.Serialization;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

public sealed class CaseV1EditRequest : ClientCase
{
    [ValidateNever]
    public CaseV1EditSource? ExpectedSource { get; init; }
}

public sealed class CaseV1EditSource
{
    [JsonRequired]
    public int Id { get; init; }
    [JsonRequired]
    public string Title { get; init; } = string.Empty;
    [JsonRequired]
    public string Description { get; init; } = string.Empty;
    [JsonRequired]
    public ClientCaseStatus Status { get; init; }

    private int? clientId, legalEntityId;
    private bool hasClientId, hasLegalEntityId;
    public int? ClientId { get => clientId; init { clientId = value; hasClientId = true; } }
    public int? LegalEntityId { get => legalEntityId; init { legalEntityId = value; hasLegalEntityId = true; } }

    // A null counterpart may be omitted by the ordinary case read serializer.
    internal bool HasObservedFields => Title is not null && Description is not null && (hasClientId || hasLegalEntityId);

    internal ClientCase ToCase() => new() { Id = Id, Title = Title, Description = Description, Status = Status, ClientId = ClientId, LegalEntityId = LegalEntityId };
}
