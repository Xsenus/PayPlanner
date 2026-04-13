namespace PayPlanner.Api.Models.Requests;

public class UpdateMainCompanySettingsRequest
{
    public List<int> CompanyIds { get; set; } = new();
    public int? DefaultCompanyId { get; set; }
}
