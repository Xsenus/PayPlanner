namespace PayPlanner.Api.Models.Responses;

public class InvoiceSummaryDto
{
    public InvoiceSummaryBucketDto Total { get; set; } = new();
    public InvoiceSummaryBucketDto Pending { get; set; } = new();
    public InvoiceSummaryBucketDto Paid { get; set; } = new();
    public InvoiceSummaryBucketDto Overdue { get; set; } = new();
    public InvoiceOverdueAgingDto OverdueAging { get; set; } = new();
}

public class InvoiceSummaryBucketDto
{
    public decimal Amount { get; set; }
    public int Count { get; set; }
}

public class InvoiceOverdueAgingDto
{
    public decimal Day0To10 { get; set; }
    public decimal Day11To30 { get; set; }
    public decimal Day31To60 { get; set; }
    public decimal Day61To90 { get; set; }
    public decimal Day91Plus { get; set; }
    public decimal Total { get; set; }
}
