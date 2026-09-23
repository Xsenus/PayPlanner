namespace PayPlanner.Api.Models.Requests;

public sealed class ContactMergeRequest
{
    public string PrimarySourceType { get; set; } = string.Empty;
    public int PrimarySourceId { get; set; }
    public string DuplicateSourceType { get; set; } = string.Empty;
    public int DuplicateSourceId { get; set; }
}
