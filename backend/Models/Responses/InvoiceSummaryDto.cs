namespace PayPlanner.Api.Models.Responses;

public class InvoiceSummaryDto
{
    public InvoiceSummaryBucketDto Total { get; set; } = new();
    public InvoiceSummaryBucketDto Pending { get; set; } = new();
    public InvoiceSummaryBucketDto Paid { get; set; } = new();
    public InvoiceSummaryBucketDto Overdue { get; set; } = new();
}

public class InvoiceSummaryBucketDto
{
    public decimal Amount { get; set; }
    public int Count { get; set; }
}
