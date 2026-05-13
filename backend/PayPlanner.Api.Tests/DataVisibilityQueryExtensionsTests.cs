using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DataVisibilityQueryExtensionsTests
{
    [Fact]
    public void ApplyClientVisibility_FiltersClientsByVisibleResponsibleUsers()
    {
        var access = CreateAccess(10, 20);
        var clients = new[]
        {
            new Client { Id = 1, ResponsibleUserId = 10 },
            new Client { Id = 2, ResponsibleUserId = 20 },
            new Client { Id = 3, ResponsibleUserId = 30 },
            new Client { Id = 4, ResponsibleUserId = null },
        };

        var result = clients
            .AsQueryable()
            .ApplyClientVisibility(access)
            .Select(client => client.Id)
            .ToArray();

        Assert.Equal(new[] { 1, 2 }, result);
    }

    [Fact]
    public void ApplyPaymentVisibility_FindsResponsibleThroughDirectAndLinkedOwners()
    {
        var access = CreateAccess(10, 20);
        var payments = new[]
        {
            new Payment
            {
                Id = 1,
                Responsibles = { new PaymentResponsible { UserId = 10 } },
            },
            new Payment
            {
                Id = 2,
                Client = new Client { ResponsibleUserId = 20 },
            },
            new Payment
            {
                Id = 3,
                Contract = new Contract
                {
                    ClientContracts =
                    {
                        new ClientContract { Client = new Client { ResponsibleUserId = 10 } },
                    },
                },
            },
            new Payment
            {
                Id = 4,
                Invoice = new Invoice
                {
                    Payments =
                    {
                        new Payment
                        {
                            DocumentKind = DocumentKind.Invoice,
                            Client = new Client { ResponsibleUserId = 10 },
                        },
                    },
                },
            },
            new Payment
            {
                Id = 5,
                LegalEntity = new LegalEntity { ResponsibleUserId = 30 },
            },
        };

        var result = payments
            .AsQueryable()
            .ApplyPaymentVisibility(access)
            .Select(payment => payment.Id)
            .ToArray();

        Assert.Equal(new[] { 1, 2, 3, 4 }, result);
    }

    [Fact]
    public void ApplyContractVisibility_FiltersContractsByLinkedCounterpartyResponsibles()
    {
        var access = CreateAccess(10, 20);
        var contracts = new[]
        {
            new Contract
            {
                Id = 1,
                ClientContracts =
                {
                    new ClientContract { Client = new Client { ResponsibleUserId = 10 } },
                },
            },
            new Contract
            {
                Id = 2,
                LegalEntityContracts =
                {
                    new LegalEntityContract { LegalEntity = new LegalEntity { ResponsibleUserId = 20 } },
                },
            },
            new Contract
            {
                Id = 3,
                ClientContracts =
                {
                    new ClientContract { Client = new Client { ResponsibleUserId = 30 } },
                },
            },
        };

        var result = contracts
            .AsQueryable()
            .ApplyContractVisibility(access)
            .Select(contract => contract.Id)
            .ToArray();

        Assert.Equal(new[] { 1, 2 }, result);
    }

    [Fact]
    public void ApplyClientVisibility_DoesNotFilterAdminOrAllAccess()
    {
        var access = new DataVisibilityAccess(
            UserId: 1,
            IsAdmin: true,
            Section: RolePermissionCatalog.Clients,
            ViewScope: DataVisibilityScope.All,
            VisibleUserIds: new[] { 1 });
        var clients = new[]
        {
            new Client { Id = 1, ResponsibleUserId = 10 },
            new Client { Id = 2, ResponsibleUserId = 20 },
            new Client { Id = 3, ResponsibleUserId = null },
        };

        var result = clients
            .AsQueryable()
            .ApplyClientVisibility(access)
            .Select(client => client.Id)
            .ToArray();

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    private static DataVisibilityAccess CreateAccess(params int[] visibleUserIds)
        => new(
            UserId: visibleUserIds[0],
            IsAdmin: false,
            Section: RolePermissionCatalog.Clients,
            ViewScope: DataVisibilityScope.Department,
            VisibleUserIds: visibleUserIds);
}
