using PayPlanner.Api.Services.LegalIntegrations.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public interface ISudrfCourtSyncService
{
    bool CanHandle(string? sourceUrl);

    Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct);
}
