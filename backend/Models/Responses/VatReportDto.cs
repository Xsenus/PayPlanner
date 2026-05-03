using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class VatReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal IncomeAmountTotal { get; init; }
    public decimal IncomeVatTotal { get; init; }
    public decimal ExpenseAmountTotal { get; init; }
    public decimal ExpenseVatTotal { get; init; }
    public decimal VatBalance { get; init; }
    public IReadOnlyList<VatReportItemDto> Rates { get; init; } = [];
}

public sealed class VatReportItemDto
{
    public decimal? VatRate { get; init; }
    public string VatRateLabel { get; init; } = string.Empty;
    public decimal IncomeBase { get; init; }
    public decimal IncomeVat { get; init; }
    public decimal IncomeGross { get; init; }
    public decimal ExpenseBase { get; init; }
    public decimal ExpenseVat { get; init; }
    public decimal ExpenseGross { get; init; }
    public decimal NetVat { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}
