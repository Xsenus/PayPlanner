using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InvoiceNumberBoundaryTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false, "max-plus-one", 1, int.MaxValue - 1, "2147483647")]
    [InlineData(true, "max-plus-one", 1, int.MaxValue - 1, "2147483647")]
    [InlineData(false, "max-plus-one", 1, int.MaxValue, null)]
    [InlineData(true, "max-plus-one", 1, int.MaxValue, null)]
    [InlineData(false, "max-plus-one", int.MaxValue, null, "2147483647")]
    [InlineData(true, "max-plus-one", int.MaxValue, null, "2147483647")]
    [InlineData(false, "first-free", int.MaxValue, null, "2147483647")]
    [InlineData(true, "first-free", int.MaxValue, null, "2147483647")]
    [InlineData(false, "first-free", int.MaxValue, int.MaxValue, null)]
    [InlineData(true, "first-free", int.MaxValue, int.MaxValue, null)]
    [InlineData(false, "first-free", 1, int.MaxValue, "1")]
    [InlineData(true, "first-free", 1, int.MaxValue, "1")]
    public async Task InvoiceBoundary_PreviewPreservesInt32RangeAndStrategyWithoutWrites(
        bool postgres, string strategy, int startNumber, int? occupiedNumber, string? expected)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        var date = new DateTime(2037, 12, 31);
        await ConfigureAsync(scope, startNumber, strategy);
        if (occupiedNumber.HasValue)
        {
            await AddActiveInvoiceAsync(scope, occupiedNumber.Value.ToString(CultureInfo.InvariantCulture), date);
        }
        var before = await SnapshotAsync(scope);

        var action = await Controller(scope).GetNextNumber(date, default);

        if (expected is null)
        {
            AssertExhausted(action.Result, date.Year);
        }
        else
        {
            var response = Assert.IsType<OkObjectResult>(action.Result);
            var body = JsonSerializer.SerializeToElement(response.Value);
            Assert.Equal(expected, body.GetProperty("nextNumber").GetString());
            Assert.Equal(date.Year, body.GetProperty("year").GetInt32());
            Assert.Equal(startNumber, body.GetProperty("startNumber").GetInt32());
            Assert.Equal(strategy, body.GetProperty("allocationStrategy").GetString());
        }
        Assert.Equal(before, await SnapshotAsync(scope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_FinalYearIncludesLastInstantAndExcludesPreviousYear(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, 1, "max-plus-one");
        await AddActiveInvoiceAsync(scope, "90", new DateTime(9998, 12, 31));
        var lastInstant = new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).AddTicks(9_999_990);
        var seeded = await AddActiveInvoiceAsync(scope, "7", lastInstant);
        var dateProperty = scope.Db.Model.FindEntityType(typeof(Invoice))!.FindProperty(nameof(Invoice.Date));
        Assert.Equal("date", dateProperty!.GetColumnType());
        var expectedStoredDate = postgres ? lastInstant.Date : lastInstant;
        Assert.Equal(expectedStoredDate, await scope.Db.Invoices.AsNoTracking()
            .Where(invoice => invoice.Id == seeded.InvoiceId).Select(invoice => invoice.Date).SingleAsync());
        var before = await SnapshotAsync(scope);

        var action = await Controller(scope).GetNextNumber(new DateTime(9999, 1, 1), default);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal("8", JsonSerializer.SerializeToElement(response.Value).GetProperty("nextNumber").GetString());
        Assert.Equal(before, await SnapshotAsync(scope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_FinalYearSupportsManualCreateUpdateAndImportSuffix(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, 1, "max-plus-one");
        var controller = Controller(scope);
        var request = Request(scope, "MANUAL-9999", new DateTime(9999, 12, 31));

        var createdResponse = await controller.Create(request, default);
        var created = Assert.IsType<InvoiceDto>(Assert.IsType<CreatedResult>(createdResponse.Result).Value);
        Assert.Equal("MANUAL-9999", created.Number);

        request.Number = "EDITED-9999";
        var updatedResponse = await controller.Update(created.Id, request, default);
        var updated = Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>(updatedResponse.Result).Value);
        Assert.Equal("EDITED-9999", updated.Number);

        var beforeDuplicate = await SnapshotAsync(scope);
        Assert.IsType<BadRequestObjectResult>((await controller.Create(request, default)).Result);
        Assert.Equal(beforeDuplicate, await SnapshotAsync(scope));

        request.AllowDuplicateNumberOnImport = true;
        var importedResponse = await controller.Create(request, default);
        var imported = Assert.IsType<InvoiceDto>(Assert.IsType<CreatedResult>(importedResponse.Result).Value);
        Assert.Equal("EDITED-9999-IMP1", imported.Number);
        Assert.NotEqual(created.Id, imported.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_FinalYearUpdateCollisionChecksOtherInvoiceAndSelf(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, 1, "max-plus-one");
        var date = new DateTime(9999, 12, 30);
        var controller = Controller(scope);
        var first = Assert.IsType<InvoiceDto>(Assert.IsType<CreatedResult>(
            (await controller.Create(Request(scope, "LEFT", date), default)).Result).Value);
        var second = Assert.IsType<InvoiceDto>(Assert.IsType<CreatedResult>(
            (await controller.Create(Request(scope, "RIGHT", date), default)).Result).Value);
        var before = await SnapshotAsync(scope);

        Assert.IsType<BadRequestObjectResult>((await controller.Update(second.Id, Request(scope, "LEFT", date), default)).Result);
        Assert.Equal(before, await SnapshotAsync(scope));
        var same = Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>(
            (await controller.Update(first.Id, Request(scope, "LEFT", date), default)).Result).Value);
        Assert.Equal("LEFT", same.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_LegacyReservedMaximumReturns422WithoutPartialPersistence(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, int.MaxValue, "first-free", admin: true);
        var date = new DateTime(2038, 6, 1);
        scope.Db.Invoices.AddRange(new Invoice { Number = "1", Date = date },
            new Invoice { Number = int.MaxValue.ToString(CultureInfo.InvariantCulture), Date = date });
        await AddLegacyPaymentAsync(scope, "1", date);
        await AddLegacyPaymentAsync(scope, "0-PREPARED", date);
        var before = await SnapshotAsync(scope);

        var response = await Controller(scope).MigrateLegacyInvoiceLinks(default);

        AssertExhausted(response.Result, date.Year);
        Assert.Equal(before, await SnapshotAsync(scope));
        Assert.DoesNotContain(await scope.Db.Invoices.AsNoTracking().Select(invoice => invoice.Number).ToArrayAsync(),
            number => number.StartsWith("-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_LegacyActiveMaximumReturns422WithoutPartialPersistence(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, 1, "max-plus-one", admin: true);
        var date = new DateTime(2039, 6, 1);
        var number = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        await AddActiveInvoiceAsync(scope, number, date, scope.Source.ForeignClient.Id);
        await AddLegacyPaymentAsync(scope, number, date);
        var before = await SnapshotAsync(scope);

        var response = await Controller(scope).MigrateLegacyInvoiceLinks(default);

        AssertExhausted(response.Result, date.Year);
        Assert.Equal(before, await SnapshotAsync(scope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceBoundary_FinalYearLegacyConflictPreservesHistoricalSourceNumber(bool postgres)
    {
        await using var scope = await ActAnnualTestScope.CreateAsync(template, postgres);
        await ConfigureAsync(scope, 1, "max-plus-one", admin: true);
        var date = new DateTime(9999, 12, 31);
        await AddActiveInvoiceAsync(scope, "9", date, scope.Source.ForeignClient.Id);
        var legacy = await AddLegacyPaymentAsync(scope, "9", date);

        Assert.IsType<OkObjectResult>((await Controller(scope).MigrateLegacyInvoiceLinks(default)).Result);

        var payment = await scope.Db.Payments.AsNoTracking().Include(item => item.Invoice)
            .SingleAsync(item => item.Id == legacy.Id);
        Assert.Equal("10", payment.Invoice!.Number);
        Assert.Equal("10", payment.Account);
        Assert.Equal("9", payment.LegacyAccountNumber);
        Assert.Equal(date, payment.LegacyAccountDate);
    }

    private static async Task ConfigureAsync(ActAnnualTestScope scope, int start, string strategy, bool admin = false)
    {
        var permission = scope.Source.Permission(RolePermissionCatalog.Accounts);
        permission.CanCreate = true;
        permission.CanEdit = true;
        permission.ViewScope = DataVisibilityScope.All;
        if (admin) scope.Source.Actor.Role!.Name = "admin";
        scope.Db.UserSettings.AddRange(
            new UserSetting { UserId = scope.Source.Actor.Id, Key = "invoices.numbering.startNumber", Value = start.ToString(CultureInfo.InvariantCulture) },
            new UserSetting { UserId = scope.Source.Actor.Id, Key = "invoices.numbering.strategy", Value = strategy });
        await scope.Db.SaveChangesAsync();
    }

    private static InvoicesController Controller(ActAnnualTestScope scope)
        => new(scope.Db, new NoopEmail(), new ActAnnualTestScope.NoopRequirements())
        {
            ControllerContext = ActAnnualTestScope.Context(scope.Source.Actor.Id)
        };

    private static UpsertInvoiceRequest Request(ActAnnualTestScope scope, string number, DateTime date)
        => new()
        {
            Number = number,
            Date = date,
            DueDate = date,
            Amount = 10m,
            ClientId = scope.Source.OwnClient.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending
        };

    private static async Task<Payment> AddActiveInvoiceAsync(ActAnnualTestScope scope, string number, DateTime date, int? clientId = null)
    {
        var payment = CreatePayment(scope, number, date);
        payment.ClientId = clientId ?? payment.ClientId;
        payment.Invoice = new Invoice { Number = number, Date = date };
        scope.Db.Payments.Add(payment);
        await scope.Db.SaveChangesAsync();
        return payment;
    }

    private static async Task<Payment> AddLegacyPaymentAsync(ActAnnualTestScope scope, string number, DateTime date)
    {
        var payment = CreatePayment(scope, number, date);
        payment.DocumentKind = DocumentKind.Payment;
        scope.Db.Payments.Add(payment);
        await scope.Db.SaveChangesAsync();
        return payment;
    }

    private static Payment CreatePayment(ActAnnualTestScope scope, string number, DateTime date)
        => new()
        {
            Account = number,
            AccountDate = date.Date,
            Date = date.Date,
            PlannedDate = date.Date,
            Amount = 10m,
            ClientId = scope.Source.OwnClient.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Invoice
        };

    private static void AssertExhausted(ActionResult? result, int year)
    {
        var response = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal($"Годовая последовательность номеров счетов за {year} год исчерпана.",
            JsonSerializer.SerializeToElement(response.Value).GetProperty("message").GetString());
    }

    private static async Task<string> SnapshotAsync(ActAnnualTestScope scope)
        => JsonSerializer.Serialize(new
        {
            Invoices = await scope.Db.Invoices.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync(),
            Payments = await scope.Db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Account,
                    item.AccountDate,
                    item.LegacyAccountNumber,
                    item.LegacyAccountDate,
                    item.InvoiceId,
                    item.ClientId,
                    item.LegalEntityId
                }).ToArrayAsync(),
            Audit = await scope.Db.ObjectAuditEvents.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.ObjectType, item.ObjectId, item.Action, item.MetadataJson }).ToArrayAsync()
        });

    private sealed class NoopEmail : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
}
