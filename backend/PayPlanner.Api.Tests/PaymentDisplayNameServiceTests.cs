using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentDisplayNameServiceTests
{
    [Fact]
    public void FillCounterpartyNames_UsesLegalEntityName_WhenStoredCounterpartyIsEmpty()
    {
        var payment = new Payment
        {
            Type = PaymentType.Income,
            CounterpartyName = string.Empty,
            LegalEntity = new LegalEntity
            {
                ShortName = "ООО \"ЛАДА - ЛЮКС\"",
                FullName = "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ЛАДА - ЛЮКС\"",
            },
        };

        PaymentDisplayNameService.FillCounterpartyNames([payment]);

        Assert.Equal("ООО \"ЛАДА - ЛЮКС\"", payment.CounterpartyName);
    }

    [Fact]
    public void FillCounterpartyNames_KeepsStoredCounterparty_WhenItIsPresent()
    {
        var payment = new Payment
        {
            Type = PaymentType.Income,
            CounterpartyName = "Ручной контрагент",
            LegalEntity = new LegalEntity
            {
                ShortName = "ООО \"ЛАДА - ЛЮКС\"",
            },
        };

        PaymentDisplayNameService.FillCounterpartyNames([payment]);

        Assert.Equal("Ручной контрагент", payment.CounterpartyName);
    }
}
