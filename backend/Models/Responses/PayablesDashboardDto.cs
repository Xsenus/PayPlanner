namespace PayPlanner.Api.Models.Responses;

public class PayablesDashboardDto
{
    public decimal TotalOutstanding { get; set; }

    public decimal OverdueOutstanding { get; set; }

    public decimal UpcomingOutstanding { get; set; }

    public int SupplierCount { get; set; }

    public int PaymentCount { get; set; }

    public List<PayablesDueBucketDto> DueBuckets { get; set; } = new();

    public List<PayablesSupplierGroupDto> Suppliers { get; set; } = new();
}

public class PayablesDueBucketDto
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public int Count { get; set; }
}

public class PayablesSupplierGroupDto
{
    public int? SupplierId { get; set; }

    public string SupplierName { get; set; } = string.Empty;

    public decimal TotalOutstanding { get; set; }

    public decimal OverdueOutstanding { get; set; }

    public DateTime? NearestDueDate { get; set; }

    public int PaymentCount { get; set; }

    public int CaseCount { get; set; }
}
