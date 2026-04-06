namespace PayPlanner.Api.Models.Responses;

public class ContractRulesSettingsDto
{
    public bool AutoGenerateWhenEmpty { get; set; } = true;

    public bool AllowManualNumberEdit { get; set; } = true;
}
