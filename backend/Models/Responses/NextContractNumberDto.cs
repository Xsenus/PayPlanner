namespace PayPlanner.Api.Models.Responses;

public class NextContractNumberDto : ContractNumberingSettingsDto
{
    public string Number { get; set; } = string.Empty;

    public bool AutoGenerateWhenEmpty { get; set; } = true;

    public bool AllowManualNumberEdit { get; set; } = true;
}
