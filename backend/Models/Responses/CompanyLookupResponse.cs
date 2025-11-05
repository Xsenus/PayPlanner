namespace PayPlanner.Api.Models.Responses;

public class CompanyLookupResponse
{
    public string FullName { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string Inn { get; set; } = string.Empty;
    public string Kpp { get; set; } = string.Empty;
    public string ActualAddress { get; set; } = string.Empty;
    public string LegalAddress { get; set; } = string.Empty;
}
