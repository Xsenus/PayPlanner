namespace PayPlanner.Api.Models.Requests;

public class ContractRulesSettingsRequest
{
    public bool AutoGenerateWhenEmpty { get; set; } = true;

    public bool AllowManualNumberEdit { get; set; } = true;
}
