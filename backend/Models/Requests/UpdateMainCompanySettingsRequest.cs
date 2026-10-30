namespace PayPlanner.Api.Models.Requests;

public class UpdateMainCompanySettingsRequest
{
    public string? ExpectedVersion { get; set; }
    public List<int> CompanyIds { get; set; } = new();
    public int? DefaultCompanyId { get; set; }
    /// <summary>Null у старого клиента сохраняет действующее правило.</summary>
    public string? SellerSnapshotTiming { get; set; }
}
