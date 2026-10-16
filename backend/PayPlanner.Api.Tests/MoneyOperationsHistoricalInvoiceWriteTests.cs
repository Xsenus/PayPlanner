using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static async Task<string> CaptureHistoricalInvoiceMoneyAsync(Data.PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Operations = await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id).Select(item => new
            {
                item.Id,
                item.Source,
                item.Direction,
                item.AccountingType,
                item.MoneyAccountId,
                item.Amount,
                item.Currency,
                item.VatRate,
                item.VatAmount,
                item.Date,
                item.LinkedInvoiceId,
                item.MatchStatus,
                item.MatchedAt,
                item.PaidAmount,
                item.PaidInvoiceNumber,
                item.PaidAccountName,
                item.BankStatementImportBatchId,
            }).ToArrayAsync(),
            Payments = await db.Payments.AsNoTracking().OrderBy(item => item.Id).Select(item => new
            {
                item.Id,
                item.InvoiceId,
                item.Amount,
                item.PaidAmount,
                item.Status,
                item.IsPaid,
                item.PaidDate,
                item.LastPaymentDate,
                item.SellerCompanyId,
            }).ToArrayAsync(),
        });

    public static IEnumerable<object[]> HistoricalInvoiceAnnotationCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "no-view", "no-edit", "foreign-company", "allowed" }
        from applied in new[] { false, true }
        select new object[] { postgres, reason, applied };

    [Theory]
    [MemberData(nameof(HistoricalInvoiceAnnotationCases))]
    public async Task HistoricalInvoiceWrite_AnnotationRoundTripPreservesLinkAndAllocation(bool postgres, string reason, bool applied)
    {
        await using var fixture = await AccountSessionsFixture.CreateAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var setup = await PrepareInvoiceScopeAsync(db, seed, reason == "allowed" ? null : reason);
        setup.Operation.LinkedInvoiceId = setup.Header.InvoiceId;
        setup.Operation.MatchStatus = applied ? MoneyOperationMatchStatus.Matched : MoneyOperationMatchStatus.None;
        setup.Operation.MatchedAt = applied ? BankScopeDate : null;
        setup.Header.PaidAmount = applied ? 10m : 0m;
        setup.Header.Status = applied ? PaymentStatus.Processing : PaymentStatus.Pending;
        setup.Header.LastPaymentDate = applied ? BankScopeDate : null;
        await db.SaveChangesAsync();
        var financialBefore = await CaptureHistoricalInvoiceMoneyAsync(db);
        var readable = reason is "allowed" or "no-edit";
        var read = Assert.IsType<OkObjectResult>(await CreateController(db, seed.Actor.Id).GetById(setup.Operation.Id, default));
        var model = Assert.IsType<MoneyOperation>(read.Value);
        Assert.Equal(readable ? setup.Header.InvoiceId : null, model.LinkedInvoiceId);
        Assert.Equal(!readable, model.IsInvoiceLinkProtected);
        model.Purpose = "SYNTHETIC-OWN-DESCRIPTION-UPDATED";

        // Repeating an ordinary edit must not apply an unallocated historical
        // pointer or apply the same ten roubles a second time.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = Assert.IsType<OkObjectResult>(await CreateController(db, seed.Actor.Id)
                .Update(setup.Operation.Id, model));
            var returned = Assert.IsType<MoneyOperation>(response.Value);
            Assert.Equal(readable ? setup.Header.InvoiceId : null, returned.LinkedInvoiceId);
            Assert.Equal(!readable, returned.IsInvoiceLinkProtected);
            await using var verify = fixture.Open();
            Assert.Equal(financialBefore, await CaptureHistoricalInvoiceMoneyAsync(verify));
            Assert.Equal(model.Purpose, await verify.MoneyOperations.AsNoTracking()
                .Where(item => item.Id == setup.Operation.Id).Select(item => item.Purpose).SingleAsync());
        }
    }

    public static IEnumerable<object[]> HistoricalInvoiceProtectedWriteCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "no-view", "no-edit" }
        from applied in new[] { false, true }
        from change in new[] { "amount", "date", "link", "status" }
        select new object[] { postgres, reason, applied, change };

    [Fact]
    public void HistoricalInvoiceWrite_ProtectedMarkerCannotBeSuppliedByCaller()
    {
        var body = System.Text.Json.JsonSerializer.Deserialize<MoneyOperation>(
            "{\"isInvoiceLinkProtected\":true,\"amount\":10}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.False(body.IsInvoiceLinkProtected);
    }

    [Theory]
    [MemberData(nameof(HistoricalInvoiceProtectedWriteCases))]
    public async Task HistoricalInvoiceWrite_NoDocumentWriteCannotAlterFinancialLink(bool postgres, string reason, bool applied, string change)
    {
        await using var fixture = await AccountSessionsFixture.CreateAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var setup = await PrepareInvoiceScopeAsync(db, fixture.Seed, reason);
        setup.Header.Invoice!.Number = "SYNTHETIC-SECRET-DOCUMENT-347281";
        setup.Operation.LinkedInvoiceId = setup.Header.InvoiceId;
        setup.Operation.MatchStatus = applied ? MoneyOperationMatchStatus.Matched : MoneyOperationMatchStatus.None;
        setup.Operation.MatchedAt = applied ? BankScopeDate : null;
        setup.Header.PaidAmount = applied ? 10m : 0m;
        setup.Header.Status = applied ? PaymentStatus.Processing : PaymentStatus.Pending;
        setup.Header.LastPaymentDate = applied ? BankScopeDate : null;
        await db.SaveChangesAsync();
        var before = await CaptureInvoiceScopeAsync(db);
        var read = Assert.IsType<OkObjectResult>(await CreateController(db, fixture.Seed.Actor.Id)
            .GetById(setup.Operation.Id, default));
        var model = Assert.IsType<MoneyOperation>(read.Value);
        switch (change)
        {
            case "amount": model.Amount += 1m; break;
            case "date": model.Date = model.Date.AddDays(1); break;
            case "link": model.LinkedInvoiceId = setup.Header.InvoiceId!.Value + 100000; break;
            case "status": model.MatchStatus = applied ? MoneyOperationMatchStatus.Ignored : MoneyOperationMatchStatus.Matched; break;
        }
        var result = await CreateController(db, fixture.Seed.Actor.Id).Update(setup.Operation.Id, model);
        Assert.True(result is ForbidResult or BadRequestObjectResult or NotFoundResult,
            $"Expected protected rejection, got {result.GetType().Name}");
        if (result is ObjectResult error)
            Assert.DoesNotContain(setup.Header.Invoice.Number!, System.Text.Json.JsonSerializer.Serialize(error.Value));
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
    }
}
