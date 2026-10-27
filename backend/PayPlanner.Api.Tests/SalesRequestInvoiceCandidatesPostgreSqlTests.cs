using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestInvoiceCandidatesPostgreSqlTests(PostgreSqlModelTemplate databases) : IClassFixture<PostgreSqlModelTemplate>
{
    [Fact]
    public async Task CandidatesRespectClientAndRejectContradictoryInvoiceGroups()
    {
        await using var lease = await databases.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var admin = new User { Email = "candidate-admin@example.test", FullName = "Тест", PasswordHash = "x", Role = new Role { Name = "admin" }, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000000" };
        var other = new Client { Name = "Другой", Phone = "+79000000001" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(admin, client, other, company);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), admin.Id, default)).Value!;
        var allowed = new Invoice { Number = "ТЕСТ-1", Date = new DateTime(2026, 1, 1) };
        var conflicting = new Invoice { Number = "ТЕСТ-2", Date = new DateTime(2026, 1, 1) };
        db.Payments.AddRange(
            new Payment { Invoice = allowed, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = conflicting, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m },
            new Payment { Invoice = conflicting, ClientId = other.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m });
        await db.SaveChangesAsync();
        var controller = new SalesRequestsController(db, service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()), new Claim(ClaimTypes.Role, "admin")], "Test"))
                }
            },
        };
        var result = await controller.InvoiceCandidates(request.Id, "ТЕСТ", 0, 20, default);
        var page = Assert.IsType<SalesRequestInvoiceCandidatesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(allowed.Id, Assert.Single(page.Items).InvoiceId);
        Assert.False(page.HasMore);
    }
}
