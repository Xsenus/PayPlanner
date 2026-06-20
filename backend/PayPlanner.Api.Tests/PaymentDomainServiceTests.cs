using System;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentDomainServiceTests
{
    [Fact]
    public void Normalize_KeepsPaymentDatesAsLocalDatesWithoutUtcMarker()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 5, 23, 30, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 6, 7, 2, 15, 0, DateTimeKind.Local),
            AccountDate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            LegacyAccountDate = new DateTime(2026, 5, 31, 23, 45, 0, DateTimeKind.Utc),
            LastPaymentDate = new DateTime(2026, 6, 6, 21, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 6, 6, 21, 0, 0, DateTimeKind.Utc),
            PaymentDocumentDate = new DateTime(2026, 6, 4, 18, 45, 0, DateTimeKind.Utc),
            Amount = 10_000m,
            PaidAmount = 10_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Payment date normalization"
        };

        PaymentDomainService.Normalize(payment);

        AssertLocalDate(payment.Date, 2026, 6, 5);
        AssertLocalDate(payment.PlannedDate, 2026, 6, 7);
        AssertLocalDate(payment.AccountDate, 2026, 6, 1);
        AssertLocalDate(payment.LegacyAccountDate, 2026, 5, 31);
        AssertLocalDate(payment.LastPaymentDate, 2026, 6, 6);
        AssertLocalDate(payment.PaidDate, 2026, 6, 6);
        AssertLocalDate(payment.PaymentDocumentDate, 2026, 6, 4);

        var json = JsonSerializer.Serialize(new
        {
            payment.Date,
            payment.PlannedDate,
            payment.AccountDate,
            payment.LegacyAccountDate,
            payment.PaidDate
        });

        Assert.Contains("\"Date\":\"2026-06-05T00:00:00\"", json);
        Assert.Contains("\"PlannedDate\":\"2026-06-07T00:00:00\"", json);
        Assert.Contains("\"AccountDate\":\"2026-06-01T00:00:00\"", json);
        Assert.Contains("\"LegacyAccountDate\":\"2026-05-31T00:00:00\"", json);
        Assert.Contains("\"PaidDate\":\"2026-06-06T00:00:00\"", json);
        Assert.DoesNotContain("Z", json);
    }

    [Fact]
    public void ApplyStatusRules_KeepsAutoCompletedPaymentDatesAsLocalDates()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 5, 22, 0, 0, DateTimeKind.Utc),
            PlannedDate = new DateTime(2026, 6, 5),
            LastPaymentDate = new DateTime(2026, 6, 6, 21, 0, 0, DateTimeKind.Utc),
            Amount = 5_000m,
            PaidAmount = 5_000m,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Description = "Completed payment dates"
        };

        PaymentDomainService.ApplyStatusRules(payment, new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(PaymentStatus.Completed, payment.Status);
        AssertLocalDate(payment.Date, 2026, 6, 6);
        AssertLocalDate(payment.LastPaymentDate, 2026, 6, 6);
        AssertLocalDate(payment.PaidDate, 2026, 6, 6);
    }

    [Fact]
    public void ApplyStatusRules_KeepsPendingPaymentDatesAsLocalDates()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 10, 23, 30, 0, DateTimeKind.Utc),
            PlannedDate = default,
            AccountDate = new DateTime(2026, 6, 8, 20, 0, 0, DateTimeKind.Utc),
            LegacyAccountDate = new DateTime(2026, 6, 7, 20, 0, 0, DateTimeKind.Utc),
            PaymentDocumentDate = new DateTime(2026, 6, 9, 20, 0, 0, DateTimeKind.Utc),
            Amount = 5_000m,
            PaidAmount = 0m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Pending payment dates"
        };

        PaymentDomainService.ApplyStatusRules(payment, new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        AssertLocalDate(payment.Date, 2026, 6, 10);
        AssertLocalDate(payment.PlannedDate, 2026, 6, 10);
        AssertLocalDate(payment.AccountDate, 2026, 6, 8);
        AssertLocalDate(payment.LegacyAccountDate, 2026, 6, 7);
        AssertLocalDate(payment.PaymentDocumentDate, 2026, 6, 9);
        Assert.Null(payment.PaidDate);
        Assert.Null(payment.LastPaymentDate);
    }

    [Fact]
    public void ApplyStatusRules_DoesNotAutoCompleteZeroAmountPayment()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 19),
            PlannedDate = new DateTime(2026, 6, 25),
            Amount = 0m,
            PaidAmount = 0m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            PaidDate = new DateTime(2026, 6, 19),
            LastPaymentDate = new DateTime(2026, 6, 19),
            Description = "Zero amount payment after payment received was cleared"
        };

        PaymentDomainService.ApplyStatusRules(payment, new DateTime(2026, 6, 19, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.False(payment.IsPaid);
        Assert.Null(payment.PaidDate);
    }

    [Fact]
    public void ApplyStatusRules_UsesProfileTimeZoneForOverdueLocalDay()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 4),
            PlannedDate = new DateTime(2026, 6, 4),
            Amount = 5_000m,
            PaidAmount = 0m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Timezone overdue payment"
        };
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");

        PaymentDomainService.ApplyStatusRules(
            payment,
            new DateTime(2026, 6, 4, 20, 30, 0, DateTimeKind.Utc),
            samara);

        Assert.Equal(PaymentStatus.Overdue, payment.Status);
        AssertLocalDate(payment.Date, 2026, 6, 4);
        AssertLocalDate(payment.PlannedDate, 2026, 6, 4);
    }

    [Fact]
    public void ApplyStatusRules_WithoutTimeZoneUsesDefaultTimeZoneForLocalDay()
    {
        var payment = new Payment
        {
            Date = new DateTime(2026, 6, 4),
            PlannedDate = new DateTime(2026, 6, 4),
            Amount = 5_000m,
            PaidAmount = 0m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Default timezone overdue payment"
        };

        PaymentDomainService.ApplyStatusRules(payment, new DateTime(2026, 6, 4, 21, 30, 0, DateTimeKind.Utc));

        Assert.Equal(PaymentStatus.Overdue, payment.Status);
        AssertLocalDate(payment.Date, 2026, 6, 4);
        AssertLocalDate(payment.PlannedDate, 2026, 6, 4);
    }

    private static void AssertLocalDate(DateTime? actual, int year, int month, int day)
    {
        Assert.True(actual.HasValue);
        AssertLocalDate(actual.Value, year, month, day);
    }

    private static void AssertLocalDate(DateTime actual, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), actual);
        Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
    }
}
