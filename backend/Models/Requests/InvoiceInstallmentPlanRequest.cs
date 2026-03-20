using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public class InvoiceInstallmentPlanRequest
{
    [Range(0, double.MaxValue)]
    public decimal Total { get; set; }

    [Range(0, double.MaxValue)]
    public decimal DownPayment { get; set; }

    [Range(0, 1000)]
    public decimal AnnualRate { get; set; }

    [Range(1, int.MaxValue)]
    public int Months { get; set; }

    [DataType(DataType.Date)]
    public DateTime StartDate { get; set; }

    public InstallmentRoundingMode RoundingMode { get; set; } = InstallmentRoundingMode.None;

    [Range(0, double.MaxValue)]
    public decimal? RoundingStep { get; set; }

    [Range(0, double.MaxValue)]
    public decimal ToPay { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Overpay { get; set; }

    [Range(0, double.MaxValue)]
    public decimal? LoanAmount { get; set; }

    [Range(0, double.MaxValue)]
    public decimal? BaseMonthlyPayment { get; set; }

    [Range(0, double.MaxValue)]
    public decimal? RoundedMonthlyPayment { get; set; }

    public List<InvoiceInstallmentPlanItemRequest> Items { get; set; } = new();
}

public class InvoiceInstallmentPlanItemRequest
{
    [Range(1, int.MaxValue)]
    public int Sequence { get; set; }

    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Principal { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Interest { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Payment { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Balance { get; set; }
}
