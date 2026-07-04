using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class QueryExtensionsSortTests
{
    [Fact]
    public void ApplyPaymentSort_SortsByCounterpartyClientColumn()
    {
        var payments = new[]
        {
            new Payment
            {
                Id = 1,
                Date = new DateTime(2026, 6, 2),
                Amount = 1000m,
                CounterpartyName = "Сергей",
            },
            new Payment
            {
                Id = 2,
                Date = new DateTime(2026, 6, 1),
                Amount = 2000m,
                Client = new Client { Name = "Анна" },
            },
        };

        var sorted = payments.AsQueryable().ApplyPaymentSort("client", "asc").ToList();

        Assert.Equal([2, 1], sorted.Select(payment => payment.Id).ToArray());
    }

    [Fact]
    public void ApplyPaymentSort_UsesLegalEntityWhenStoredCounterpartyIsEmpty()
    {
        var payments = new[]
        {
            new Payment
            {
                Id = 1,
                CounterpartyName = string.Empty,
                LegalEntity = new LegalEntity { ShortName = "ООО Лада" },
            },
            new Payment
            {
                Id = 2,
                CounterpartyName = "Аванс",
            },
        };

        var sorted = payments.AsQueryable().ApplyPaymentSort("client", "asc").ToList();

        Assert.Equal([2, 1], sorted.Select(payment => payment.Id).ToArray());
    }

    [Fact]
    public void ApplyPaymentSort_SortsByPaidDate()
    {
        var payments = new[]
        {
            new Payment
            {
                Id = 1,
                Date = new DateTime(2026, 6, 1),
                PaidDate = new DateTime(2026, 6, 5),
            },
            new Payment
            {
                Id = 2,
                Date = new DateTime(2026, 6, 1),
                LastPaymentDate = new DateTime(2026, 6, 3),
            },
        };

        var sorted = payments.AsQueryable().ApplyPaymentSort("paidDate", "desc").ToList();

        Assert.Equal([1, 2], sorted.Select(payment => payment.Id).ToArray());
    }
}
