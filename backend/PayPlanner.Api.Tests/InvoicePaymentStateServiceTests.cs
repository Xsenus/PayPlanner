using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicePaymentStateServiceTests
{
    [Fact]
    public void EvaluateInvoiceAggregate_IgnoresCancelledRows_WhenSelectingStatusSources()
    {
        var payments = new List<Payment>
        {
            CreateInvoiceRow(439, 25_000m, PaymentStatus.Cancelled),
            CreateInvoiceRow(440, 15_000m, PaymentStatus.Completed, paidAmount: 15_000m),
            CreateInvoiceRow(1843, 10_000m, PaymentStatus.Completed, paidAmount: 10_000m),
            CreateInvoiceRow(1926, 0.10m, PaymentStatus.Cancelled),
            CreateInvoiceRow(1927, 1m, PaymentStatus.Cancelled),
            CreateInvoiceRow(1928, 100m, PaymentStatus.Cancelled),
            CreateInvoiceRow(1929, 25_000m, PaymentStatus.Cancelled),
            CreateInvoiceRow(1930, 25_000m, PaymentStatus.Cancelled),
        };

        var evaluation = InvoicePaymentStateService.EvaluateInvoiceAggregate(
            payments,
            new DateTime(2026, 5, 15));

        Assert.NotNull(evaluation);
        Assert.Equal(440, evaluation!.RepresentativePaymentId);
        Assert.Equal(2, evaluation.SourcePaymentCount);
        Assert.Equal(PaymentStatus.Completed, evaluation.Suggested.Status);
    }

    private static Payment CreateInvoiceRow(
        int id,
        decimal amount,
        PaymentStatus status,
        decimal paidAmount = 0m)
    {
        return new Payment
        {
            Id = id,
            InvoiceId = 81,
            Account = "8",
            AccountDate = new DateTime(2026, 2, 2),
            Date = new DateTime(2026, 2, 5),
            PlannedDate = new DateTime(2026, 2, 5),
            Amount = amount,
            PaidAmount = paidAmount,
            Status = status,
            IsPaid = status == PaymentStatus.Completed,
            PaidDate = status == PaymentStatus.Completed ? new DateTime(2026, 2, 5) : null,
            LastPaymentDate = status == PaymentStatus.Completed ? new DateTime(2026, 2, 5) : null,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
        };
    }
}
