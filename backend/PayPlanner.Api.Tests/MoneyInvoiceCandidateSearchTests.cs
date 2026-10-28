using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneyInvoiceCandidateSearchTests
{
    private sealed class Recorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public Func<DbCommand, Task>? BeforeHeaderPage { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (BeforeHeaderPage is { } before && command.CommandText.Contains("invoice-candidate-header-page", StringComparison.Ordinal))
            {
                BeforeHeaderPage = null;
                await before(command);
            }
            return result;
        }
    }

    [Fact]
    public async Task Sqlite_SearchesBeyondSuggestionsWithoutChangingMoneyOrPermissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var recorder = new Recorder();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(recorder).Options);
        await VerifyAsync(db, recorder, racesOnly: false);
    }

    [Fact]
    public async Task Sqlite_RejectsChangedOperationAndRevokedPermissionsDuringPageRead()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var recorder = new Recorder();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(recorder).Options);
        await VerifyAsync(db, recorder, racesOnly: true);
    }

    [Fact]
    public async Task Postgres_SearchesBeyondSuggestionsWithoutChangingMoneyOrPermissions()
        => await VerifyPostgresAsync(racesOnly: false);

    [Fact]
    public async Task Postgres_RejectsChangedOperationAndRevokedPermissionsDuringPageRead()
        => await VerifyPostgresAsync(racesOnly: true);

    private static async Task VerifyPostgresAsync(bool racesOnly)
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci",
                Password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD") };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci"
            || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("An existing loopback PostgreSQL server and isolated payplanner_ci role are required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_invoice_search_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_invoice_search_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var role = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await role.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            var recorder = new Recorder();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString).AddInterceptors(recorder).Options);
            await VerifyAsync(db, recorder, racesOnly);
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_invoice_search_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task VerifyAsync(PaymentContext db, Recorder recorder, bool racesOnly)
    {
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "invoice-search", Description = "Synthetic employee" };
        var user = new User { Role = role, Email = "invoice-search@fixture.local", FullName = "Synthetic", PasswordHash = "hash",
            IsEmployee = true, IsActive = true, IsApproved = true };
        var own = new LegalEntity { ShortName = "Own" };
        var foreign = new LegalEntity { ShortName = "Other" };
        var account = new MoneyAccount { Company = own, Name = "Synthetic bank", Source = MoneyOperationSource.Bank, Currency = "RUB", IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        db.LegalEntities.Add(foreign);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Accounts, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.Own });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = own.Id });
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        var date = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        var operation = new MoneyOperation { MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact, Amount = 10m, Currency = "RUB", Date = date, Purpose = "Без номера документа", ResponsibleUserId = user.Id };
        db.MoneyOperations.Add(operation);
        Payment Header(string number, int companyId, bool visible = true, decimal amount = 100m) => new()
        {
            Invoice = new Invoice { Number = number, Date = date }, Account = number, SellerCompanyId = companyId,
            Date = date, DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income, Status = PaymentStatus.Pending, Amount = amount,
            Responsibles = visible ? new List<PaymentResponsible> { new() { UserId = user.Id } } : new List<PaymentResponsible>(),
        };
        var headers = Enumerable.Range(1, 220).Select(i => Header($"СЧ-{i:D3}", own.Id)).ToArray();
        var target = headers[104];
        target.Invoice!.Number = target.Account = "К-ЁЖ-105%_\\'";
        target.CounterpartyName = "ООО Ёжик";
        var contractHeader = headers[199];
        contractHeader.Contract = new Contract { Number = "ДОГ-ЁЖ-200", Date = date, SellerCompanyId = own.Id };
        headers[120].PaidAmount = 100m;
        headers[120].Status = PaymentStatus.Completed;
        headers[141].Status = PaymentStatus.Cancelled;
        var foreignHeader = Header("Секрет-чужой", foreign.Id);
        var hiddenHeader = Header("Секрет-скрытый", own.Id, visible: false);
        var unfit = Header("Край-суммы", own.Id, amount: 1m);
        db.Payments.AddRange(headers);
        db.Payments.AddRange(foreignHeader, hiddenHeader, unfit);
        var duplicateHeader = Header(headers[210].Account!, own.Id);
        duplicateHeader.Invoice = headers[210].Invoice;
        db.Payments.Add(duplicateHeader);
        await db.SaveChangesAsync();
        var actorId = user.Id;
        var operationId = operation.Id;
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        MoneyOperationsController Controller() => new(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) }, "Test")) } },
        };
        async Task<IReadOnlyList<MoneyOperationInvoiceCandidateDto>> ReadAsync(string? search, int? expectedAggregateReads = null)
        {
            recorder.Commands.Clear();
            var result = search is null ? await Controller().GetInvoiceCandidates(operationId, default)
                : await Controller().SearchInvoiceCandidates(operationId, default, search);
            var items = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.InRange(items.Count, 0, 10);
            // Three bounded 100-header pages and one empty read, not one query per invoice.
            var pages = recorder.Commands.Where(command => command.Contains("invoice-candidate-header-page", StringComparison.Ordinal)).ToArray();
            Assert.Equal(4, pages.Length);
            Assert.All(pages, command => Assert.Contains("LIMIT", command));
            if (expectedAggregateReads.HasValue)
                Assert.Equal(expectedAggregateReads.Value, recorder.Commands.Count(command => command.Contains("invoice-candidate-payment-aggregate", StringComparison.Ordinal)));
            Assert.True(recorder.Commands.Count < 30, $"Unexpected query growth: {recorder.Commands.Count}");
            return items;
        }
        if (racesOnly)
        {
            void ChangeOnFirstHeader(string sql, object value, int id)
                => recorder.BeforeHeaderPage = async query =>
                {
                    await using var mutation = query.Connection!.CreateCommand();
                    mutation.Transaction = query.Transaction;
                    mutation.CommandText = sql;
                    var fieldValue = mutation.CreateParameter(); fieldValue.ParameterName = "value"; fieldValue.Value = value;
                    var rowId = mutation.CreateParameter(); rowId.ParameterName = "id"; rowId.Value = id;
                    mutation.Parameters.Add(fieldValue); mutation.Parameters.Add(rowId);
                    Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
                };
            ChangeOnFirstHeader("UPDATE \"MoneyOperations\" SET \"Purpose\" = @value WHERE \"Id\" = @id", "Changed during read", operationId);
            Assert.IsType<ConflictObjectResult>((await Controller().GetInvoiceCandidatePage(operationId, default)).Result);
            await db.MoneyOperations.Where(item => item.Id == operationId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Purpose, "Без номера документа"));
            var moneyPermissionId = await db.RolePermissions.Where(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Payments).Select(item => item.Id).SingleAsync();
            ChangeOnFirstHeader("UPDATE \"RolePermissions\" SET \"CanEdit\" = @value WHERE \"Id\" = @id", false, moneyPermissionId);
            Assert.IsType<ForbidResult>((await Controller().GetInvoiceCandidatePage(operationId, default)).Result);
            await db.RolePermissions.Where(item => item.Id == moneyPermissionId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanEdit, true));
            var invoicePermissionId = await db.RolePermissions.Where(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Accounts).Select(item => item.Id).SingleAsync();
            ChangeOnFirstHeader("UPDATE \"RolePermissions\" SET \"CanView\" = @value WHERE \"Id\" = @id", false, invoicePermissionId);
            Assert.IsType<ForbidResult>((await Controller().GetInvoiceCandidatePage(operationId, default)).Result);
            await db.RolePermissions.Where(item => item.Id == invoicePermissionId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, true));
            Assert.Null(recorder.BeforeHeaderPage);
            db.ChangeTracker.Clear();
            Assert.Equal(before, await SnapshotAsync(db));
            return;
        }

        var defaults = await ReadAsync(null, expectedAggregateReads: 3);
        Assert.Equal(10, defaults.Count);
        Assert.DoesNotContain(defaults, candidate => candidate.InvoiceId == target.InvoiceId);
        Assert.Equal(target.InvoiceId, Assert.Single(await ReadAsync("  ёж-105  ", expectedAggregateReads: 1)).InvoiceId);
        Assert.Equal(target.InvoiceId, Assert.Single(await ReadAsync("ООО ёЖиК")).InvoiceId);
        Assert.Equal(target.InvoiceId, Assert.Single(await ReadAsync("%_\\'")).InvoiceId);
        Assert.Equal(contractHeader.InvoiceId, Assert.Single(await ReadAsync("дог-ёж-200")).InvoiceId);
        Assert.Empty(await ReadAsync("не найдено", expectedAggregateReads: 0));
        Assert.Empty(await ReadAsync("Секрет", expectedAggregateReads: 0));
        Assert.Empty(await ReadAsync("СЧ-121"));
        Assert.Empty(await ReadAsync("СЧ-142"));
        var tooLarge = Assert.Single(await ReadAsync("Край-суммы"));
        Assert.Equal(0, tooLarge.Score);
        Assert.IsType<BadRequestObjectResult>(await Controller().LinkInvoice(operationId,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = unfit.InvoiceId!.Value }, default));
        Assert.Equal(defaults.Select(item => item.InvoiceId), (await ReadAsync("   ")).Select(item => item.InvoiceId));
        Assert.IsType<BadRequestObjectResult>((await Controller().SearchInvoiceCandidates(operationId, default, new string('a', 201))).Result);
        Assert.IsType<BadRequestObjectResult>((await Controller().SearchInvoiceCandidates(operationId, default, "bad\0query")).Result);
        MoneyOperationInvoiceCandidatePageDto Page(ActionResult<MoneyOperationInvoiceCandidatePageDto> response)
            => Assert.IsType<MoneyOperationInvoiceCandidatePageDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var first = Page(await Controller().GetInvoiceCandidatePage(operationId, default));
        Assert.True(first.HasMore);
        Assert.Null(first.Search);
        Assert.Null(first.Cursor);
        Assert.InRange(first.NextCursor!.Length, 1, 1024);
        Assert.Equal(defaults.Select(item => item.PaymentId), first.Items.Select(item => item.PaymentId));
        var visited = new List<(int InvoiceId, int PaymentId)>();
        var page = first;
        var pagesRead = 0;
        while (true)
        {
            pagesRead++;
            Assert.InRange(page.Items.Count, 1, 10);
            visited.AddRange(page.Items.Select(item => (item.InvoiceId, item.PaymentId)));
            if (!page.HasMore) { Assert.Null(page.NextCursor); break; }
            Assert.True(pagesRead < 30, "Pagination did not terminate.");
            var nextCursor = page.NextCursor;
            page = Page(await Controller().GetInvoiceCandidatePage(operationId, default, cursor: nextCursor));
            Assert.Equal(nextCursor, page.Cursor);
        }
        var expected = headers.Where(item => item.Status is not (PaymentStatus.Completed or PaymentStatus.Cancelled))
            .Append(duplicateHeader).Select(item => (item.InvoiceId!.Value, item.Id)).OrderByDescending(item => item.Value).ThenByDescending(item => item.Id).ToArray();
        Assert.Equal(expected, visited);
        Assert.Equal(219, visited.Count);
        Assert.Equal(visited.Count, visited.Distinct().Count());
        Assert.Equal(22, pagesRead);
        var replay = Page(await Controller().GetInvoiceCandidatePage(operationId, default, cursor: first.NextCursor));
        Assert.Equal(visited.Skip(10).Take(10), replay.Items.Select(item => (item.InvoiceId, item.PaymentId)));
        Assert.IsType<ConflictObjectResult>((await Controller().GetInvoiceCandidatePage(operationId, default, search: "другой", cursor: first.NextCursor)).Result);
        foreach (var invalidCursor in new[] { "not!base64", new string('a', 1025), "e30" })
            Assert.IsType<BadRequestObjectResult>((await Controller().GetInvoiceCandidatePage(operationId, default, cursor: invalidCursor)).Result);
        await db.MoneyOperations.Where(item => item.Id == operationId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Purpose, "Изменено параллельно"));
        Assert.IsType<ConflictObjectResult>((await Controller().GetInvoiceCandidatePage(operationId, default, cursor: first.NextCursor)).Result);
        await db.MoneyOperations.Where(item => item.Id == operationId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Purpose, "Без номера документа"));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(db));
        await db.RolePermissions.Where(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanEdit, false));
        Assert.IsType<ForbidResult>((await Controller().SearchInvoiceCandidates(operationId, default, "ёж")).Result);
        Assert.IsType<ForbidResult>((await Controller().GetInvoiceCandidatePage(operationId, default, cursor: first.NextCursor)).Result);
        await db.RolePermissions.Where(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanEdit, true));
        await db.UserCompanyAccesses.Where(item => item.UserId == actorId).ExecuteDeleteAsync();
        Assert.IsType<NotFoundResult>((await Controller().SearchInvoiceCandidates(operationId, default, "ёж")).Result);
        Assert.IsType<NotFoundResult>((await Controller().GetInvoiceCandidatePage(operationId, default, cursor: first.NextCursor)).Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    private static async Task<string> SnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Operations = await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        { item.Id, item.MoneyAccountId, item.Amount, item.Currency, item.Date, item.LinkedInvoiceId, item.MatchStatus, item.Purpose }).ToArrayAsync(),
        Payments = await db.Payments.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        { item.Id, item.InvoiceId, item.SellerCompanyId, item.Type, item.Amount, item.PaidAmount, item.Status, item.SystemNotes }).ToArrayAsync(),
    });
}
