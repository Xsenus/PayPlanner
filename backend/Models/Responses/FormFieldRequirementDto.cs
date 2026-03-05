namespace PayPlanner.Api.Models.Responses;

public class FormFieldRequirementDto
{
    public string Section { get; set; } = string.Empty;
    public string FieldKey { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public DateTime UpdatedAt { get; set; }
}
