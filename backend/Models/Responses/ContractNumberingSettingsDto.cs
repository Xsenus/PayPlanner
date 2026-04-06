namespace PayPlanner.Api.Models.Responses;

public class ContractNumberingSettingsDto
{
    public int StartNumber { get; set; } = 1;

    public string AllocationStrategy { get; set; } = "max-plus-one";

    public string Template { get; set; } = "{N}-{DD}/{MM}-{YY}";
}
