using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private sealed record InvoiceScopeSeed(MoneyOperation Operation, Payment Header, Payment? Installment);

    private static async Task<string> CaptureInvoiceScopeAsync(PaymentContext db) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Manual = await CaptureManualCompanyAsync(db),
        Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(payment => payment.Id).Select(payment => new
        {
            payment.Id,
            payment.InvoiceId,
            payment.DocumentKind,
            payment.Type,
            payment.SellerCompanyId,
            payment.Amount,
            payment.PaidAmount,
            payment.Status,
            payment.IsPaid,
            payment.PaidDate,
            payment.LastPaymentDate,
        }).ToArrayAsync(),
    });

    private static async Task<InvoiceScopeSeed> PrepareInvoiceScopeAsync(PaymentContext db, AccountScopeSeed seed,
        string? reason = null, PaymentType direction = PaymentType.Income)
    {
        await PrepareManualCompanyAsync(db, seed);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = seed.Actor.RoleId,
            Section = RolePermissionCatalog.Accounts,
            CanView = reason != "no-view",
            CanEdit = reason != "no-edit",
            ViewScope = DataVisibilityScope.Own,
        });
        var operation = ManualCompanyOperation(seed, seed.Own.Id);
        operation.Direction = direction == PaymentType.Expense ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        operation.Purpose = "Оплата по счёту №501";
        var header = new Payment
        {
            Invoice = new Invoice { Number = "501", Date = BankScopeDate },
            Account = "501",
            DocumentKind = DocumentKind.Invoice,
            Type = reason == "wrong-direction" ? PaymentType.Expense : direction,
            Status = reason == "cancelled" ? PaymentStatus.Cancelled : PaymentStatus.Pending,
            Amount = 20m,
            Date = BankScopeDate,
            SellerCompanyId = reason == "foreign-company" ? seed.Other.Id : reason == "unknown-company" ? null : seed.Company.Id,
        };
        if (reason != "hidden-header") header.Responsibles.Add(new PaymentResponsible { UserId = seed.Actor.Id });
        Payment? installment = null;
        if (reason is "inherited-installment" or "foreign-installment" or "mixed-direction")
        {
            installment = new Payment
            {
                Invoice = header.Invoice,
                DocumentKind = DocumentKind.Payment,
                Type = reason == "mixed-direction" ? PaymentType.Expense : PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 20m,
                Date = BankScopeDate,
                SellerCompanyId = reason == "foreign-installment" ? seed.Other.Id : seed.Company.Id,
            };
            if (reason != "inherited-installment") installment.Responsibles.Add(new PaymentResponsible { UserId = seed.Actor.Id });
            db.Payments.Add(installment);
        }
        if (reason == "no-document") db.Invoices.Add(header.Invoice!);
        else db.Payments.Add(header);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        if (reason == "no-document") header.InvoiceId = header.Invoice!.Id;
        return new(operation, header, installment);
    }

    private static async Task<IActionResult?> InvokeInvoiceScopeAsync(PaymentContext db, AccountScopeSeed seed,
        InvoiceScopeSeed invoice, string endpoint)
    {
        var controller = CreateController(db, seed.Actor.Id);
        if (endpoint == "candidates") return (await controller.GetInvoiceCandidates(invoice.Operation.Id, default)).Result;
        if (endpoint == "link") return await controller.LinkInvoice(invoice.Operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Header.InvoiceId!.Value }, default);
        var model = ManualCompanyOperation(seed, seed.Own.Id);
        model.Direction = invoice.Operation.Direction;
        model.LinkedInvoiceId = invoice.Header.InvoiceId;
        return endpoint == "create" ? await controller.Create(model) : await controller.Update(invoice.Operation.Id, model);
    }

    public static IEnumerable<object[]> InvoiceScopeDeniedCases =>
        from postgres in new[] { false, true }
        from endpoint in new[] { "candidates", "link", "create", "update" }
        from reason in new[] { "no-view", "foreign-company", "unknown-company", "hidden-header", "foreign-installment", "mixed-direction", "no-document", "cancelled", "wrong-direction" }
        select new object[] { postgres, endpoint, reason };

    [Theory]
    [MemberData(nameof(InvoiceScopeDeniedCases))]
    public async Task InvoiceScope_DeniedDocumentNeverLeaksOrChangesMoney(bool postgres, string endpoint, string reason)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var invoice = await PrepareInvoiceScopeAsync(db, fixture.Seed, reason);
        var before = await CaptureInvoiceScopeAsync(db);
        var result = await InvokeInvoiceScopeAsync(db, fixture.Seed, invoice, endpoint);
        if (endpoint == "candidates" && result is OkObjectResult ok)
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
        else Assert.True(result is NotFoundResult or ForbidResult or BadRequestObjectResult,
            $"Expected a protected rejection, got {result?.GetType().Name}");
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
    }

    public static IEnumerable<object[]> InvoiceScopeAllowedCases =>
        from postgres in new[] { false, true }
        from admin in new[] { false, true }
        from endpoint in new[] { "candidates", "link", "create", "update" }
        from direction in new[] { PaymentType.Income, PaymentType.Expense }
        select new object[] { postgres, admin, endpoint, direction };

    [Theory]
    [MemberData(nameof(InvoiceScopeAllowedCases))]
    public async Task InvoiceScope_AuthorizedOwnDocumentPreservesPartialAllocation(bool postgres, bool admin, string endpoint, PaymentType direction)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin);
        await using var db = fixture.Open();
        var invoice = await PrepareInvoiceScopeAsync(db, fixture.Seed, direction: direction);
        var before = await CaptureInvoiceScopeAsync(db);
        var result = await InvokeInvoiceScopeAsync(db, fixture.Seed, invoice, endpoint);
        if (endpoint == "candidates")
        {
            var candidates = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(result).Value);
            var candidate = Assert.Single(candidates);
            Assert.Equal(invoice.Header.InvoiceId, candidate.InvoiceId);
            Assert.Equal(20m, candidate.OutstandingAmount);
            Assert.Equal(0m, candidate.PaidAmount);
            await using var verify = fixture.Open();
            Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
        }
        else
        {
            Assert.True(result is OkObjectResult or CreatedResult, $"Expected success, got {result?.GetType().Name}");
            await using var verify = fixture.Open();
            var payment = await verify.Payments.SingleAsync();
            Assert.Equal(10m, payment.PaidAmount);
            Assert.Equal(direction, payment.Type);
            Assert.Equal(PaymentStatus.Processing, payment.Status);
            Assert.False(payment.IsPaid);
            Assert.Equal(BankScopeDate, payment.LastPaymentDate);
            var operation = await verify.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == payment.InvoiceId);
            Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
            Assert.Equal(fixture.Seed.Own.Id, operation.MoneyAccountId);
            Assert.Equal(10m, operation.Amount);
        }
    }

    [Theory]
    [InlineData(false, "link")]
    [InlineData(true, "link")]
    [InlineData(false, "create")]
    [InlineData(true, "create")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    public async Task InvoiceScope_ReadOnlyAccountsAllowsCandidatesButCannotAllocate(bool postgres, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var invoice = await PrepareInvoiceScopeAsync(db, fixture.Seed, "no-edit");
        var before = await CaptureInvoiceScopeAsync(db);
        var candidateResult = await InvokeInvoiceScopeAsync(db, fixture.Seed, invoice, "candidates");
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(candidateResult).Value));
        var linkResult = await InvokeInvoiceScopeAsync(db, fixture.Seed, invoice, endpoint);
        Assert.True(linkResult is ForbidResult or BadRequestObjectResult or NotFoundResult);
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceScope_BestOlderMatchIsNotLostBeyondFirstReadBatch(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var setup = await PrepareInvoiceScopeAsync(db, seed);
        setup.Operation.Purpose = "Оплата по счёту №999";
        Payment Candidate(string number, DateTime date)
        {
            var payment = new Payment
            {
                Invoice = new Invoice { Number = number, Date = date },
                Account = number,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 20m,
                Date = date,
                SellerCompanyId = seed.Company.Id,
            };
            payment.Responsibles.Add(new PaymentResponsible { UserId = seed.Actor.Id });
            return payment;
        }
        for (var index = 0; index < 201; index++) db.Payments.Add(Candidate((600 + index).ToString(), BankScopeDate));
        await db.SaveChangesAsync();
        var wanted = Candidate("999", BankScopeDate.AddYears(-2));
        db.Payments.Add(wanted);
        await db.SaveChangesAsync();
        var result = await CreateController(db, seed.Actor.Id).GetInvoiceCandidates(setup.Operation.Id, default);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(10, candidates.Count);
        Assert.Equal(wanted.InvoiceId, candidates[0].InvoiceId);
        Assert.Equal(20m, candidates[0].OutstandingAmount);
    }

    [Theory]
    [InlineData(false, "candidates")]
    [InlineData(true, "candidates")]
    [InlineData(false, "link")]
    [InlineData(true, "link")]
    [InlineData(false, "create")]
    [InlineData(true, "create")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    public async Task InvoiceScope_InstallmentInheritsExistingHeaderVisibility(bool postgres, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var invoice = await PrepareInvoiceScopeAsync(db, fixture.Seed, "inherited-installment");
        var access = await db.ResolveSectionAccessAsync(CreateController(db, fixture.Seed.Actor.Id).User,
            RolePermissionCatalog.Accounts, default, requireApprovedUser: true);
        Assert.NotNull(access);
        Assert.True(await db.Payments.ApplyPaymentVisibility(access.Visibility).AnyAsync(payment => payment.Id == invoice.Installment!.Id));
        var before = await CaptureInvoiceScopeAsync(db);
        var result = await InvokeInvoiceScopeAsync(db, fixture.Seed, invoice, endpoint);
        Assert.True(result is OkObjectResult or CreatedResult);
        await using var verify = fixture.Open();
        if (endpoint == "candidates")
        {
            var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(result).Value));
            Assert.Equal(20m, candidate.OutstandingAmount);
            Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
        }
        else
        {
            var payments = await verify.Payments.ToListAsync();
            Assert.Equal(2, payments.Count);
            Assert.All(payments, payment => { Assert.Equal(10m, payment.PaidAmount); Assert.Equal(PaymentStatus.Processing, payment.Status); });
            var operation = await verify.MoneyOperations.SingleAsync(item => item.LinkedInvoiceId == invoice.Header.InvoiceId);
            Assert.Equal(10m, operation.Amount);
            Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        }
    }
}
