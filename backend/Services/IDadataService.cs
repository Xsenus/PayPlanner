using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IDadataService
{
    Task<CompanyLookupResponse?> FindCompanyByInnAsync(string inn, CancellationToken ct = default);
}
