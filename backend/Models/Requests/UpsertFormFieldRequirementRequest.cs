namespace PayPlanner.Api.Models.Requests;

public class UpsertFormFieldRequirementRequest
{
    public string Section { get; set; } = string.Empty;
    public string FieldKey { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
}
