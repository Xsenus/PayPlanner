namespace PayPlanner.Api.Models.Requests;

public class SpreadsheetImportApplyRequest
{
    public List<string> SelectedPreviewIds { get; set; } = new();
    public List<SpreadsheetImportCounterpartyOptionRequest> CounterpartyOptions { get; set; } = new();
    public bool AllowUpdates { get; set; }
    public bool AllowClearingExistingValues { get; set; }
}

public class SpreadsheetImportCounterpartyOptionRequest
{
    public string PreviewId { get; set; } = string.Empty;
    public string ImportMode { get; set; } = string.Empty;
}
