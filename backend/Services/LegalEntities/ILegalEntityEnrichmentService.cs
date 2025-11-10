using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PayPlanner.Api.Services.LegalEntities;

public interface ILegalEntityEnrichmentService
{
    Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken);
}

public record LegalEntitySuggestion(
    string ShortName,
    string? FullName,
    string? Inn,
    string? Kpp,
    string? Ogrn,
    string? Address,
    string? Phone,
    string? Email,
    string? Director
);
