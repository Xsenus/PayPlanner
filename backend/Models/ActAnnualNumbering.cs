namespace PayPlanner.Api.Models;

public sealed class ActAnnualNumberCounter
{
    public int Year { get; set; }
    public long LastNumber { get; set; }
}

// Retained independently of the act so deleting it cannot reopen a known request.
public sealed class ActCreateReceipt
{
    public int ActorUserId { get; set; }
    public Guid UploadKey { get; set; }
    public string Operation { get; set; } = "ordinary";
    public string CommandSha256 { get; set; } = string.Empty;
    public int ActId { get; set; }
    public int? CaseId { get; set; }
    public int? WorkOrderId { get; set; }
    public int? PublishedDocumentId { get; set; }
}
