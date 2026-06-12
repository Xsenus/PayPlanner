using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentAdvanceReviewServiceTests
{
    [Fact]
    public void IsUnallocatedAdvanceCandidate_ReturnsTrue_ForPaidIncomeClientPaymentWithoutDocuments()
    {
        var payment = CreateCandidate();

        Assert.True(PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment));
    }

    [Fact]
    public void IsUnallocatedAdvanceCandidate_ReturnsTrue_ForPaidIncomeLegalEntityPaymentWithoutDocuments()
    {
        var payment = CreateCandidate();
        payment.ClientId = null;
        payment.LegalEntityId = 20;

        Assert.True(PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment));
    }

    [Fact]
    public void IsUnallocatedAdvanceCandidate_ReturnsTrue_ForPartiallyPaidIncomePayment()
    {
        var payment = CreateCandidate(payment =>
        {
            payment.Amount = 10_000m;
            payment.PaidAmount = 4_000m;
            payment.IsPaid = false;
            payment.Status = PaymentStatus.Processing;
        });

        Assert.True(PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment));
        Assert.Equal(4_000m, PaymentAdvanceReviewService.GetAdvanceAmount(payment));
    }

    [Fact]
    public void IsUnallocatedAdvanceCandidate_ReturnsTrue_WhenPaymentHasCaseButNoClosingDocuments()
    {
        var payment = CreateCandidate(payment => payment.ClientCaseId = 501);

        Assert.True(PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment));
    }

    [Theory]
    [MemberData(nameof(NotCandidateCases))]
    public void IsUnallocatedAdvanceCandidate_ReturnsFalse_ForPaymentsOutsideRule(Payment payment)
        => Assert.False(PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment));

    [Fact]
    public void UnallocatedAdvanceCandidatePredicate_MatchesInMemoryRule_ForCoreScenarios()
    {
        var predicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate().Compile();
        var payments = new[]
        {
            CreateCandidate(),
            CreateCandidate(payment => payment.Account = "INV-1"),
            CreateCandidate(payment => payment.Type = PaymentType.Expense),
            CreateCandidate(payment => payment.Amount = 0m, paidAmount: 0m),
            CreateCandidate(payment =>
            {
                payment.ClientId = null;
                payment.LegalEntityId = 20;
            }),
        };

        foreach (var payment in payments)
        {
            Assert.Equal(
                PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment),
                predicate(payment));
        }
    }

    public static IEnumerable<object[]> NotCandidateCases()
    {
        yield return [CreateCandidate(payment => payment.Type = PaymentType.Expense)];
        yield return [CreateCandidate(payment => payment.Status = PaymentStatus.Cancelled)];
        yield return [CreateCandidate(payment => payment.InvoiceId = 10)];
        yield return [CreateCandidate(payment => payment.ContractId = 11)];
        yield return [CreateCandidate(payment => payment.Acts.Add(new Act { Number = "ACT-12", Date = payment.Date }))];
        yield return [CreateCandidate(payment => payment.Account = "INV-12")];
        yield return [CreateCandidate(payment => payment.LegacyAccountNumber = "OLD-12")];
        yield return [CreateCandidate(payment => payment.DocumentKind = DocumentKind.Invoice)];
        yield return [CreateCandidate(payment => payment.Amount = 0m, paidAmount: 0m)];
        yield return [CreateCandidate(payment =>
        {
            payment.ClientId = 10;
            payment.LegalEntityId = 20;
        })];
        yield return [CreateCandidate(payment => payment.ClientId = null)];
    }

    private static Payment CreateCandidate(Action<Payment>? configure = null, decimal paidAmount = 1_000m)
    {
        var payment = new Payment
        {
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            Amount = paidAmount,
            PaidAmount = paidAmount,
            IsPaid = paidAmount > 0m,
            ClientId = 10,
            Date = new DateTime(2026, 3, 2),
            Description = "Нераспределенный платеж",
        };

        configure?.Invoke(payment);
        return payment;
    }
}
