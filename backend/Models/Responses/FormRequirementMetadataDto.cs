namespace PayPlanner.Api.Models.Responses;

public class FormRequirementFieldMetadataDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Category { get; set; }
}

public class FormRequirementSectionMetadataDto
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<FormRequirementFieldMetadataDto> Fields { get; set; } = new();
}
