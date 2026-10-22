using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    [Fact]
    public async Task ImportOwnership_ActsWithSameNumberAndDateRemainSeparateForDifferentClients()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var files = new[] { CreateActFile(
            new[] { "Дата акта", "№ акта", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "SAME-ACT", "100", "First owner", "Подписан" },
            new[] { "01.06.2026", "SAME-ACT", "200", "Second owner", "Подписан" }) };
        var preview = await service.PreviewAsync(files);
        Assert.Equal(2, Assert.Single(preview.Sections, section => section.Key == "acts").SelectableRows);
        await service.ApplyAsync(files, SelectOwnershipRows(preview));
        var repeated = await service.PreviewAsync(files);
        Assert.DoesNotContain(Assert.Single(repeated.Sections, section => section.Key == "acts").Rows,
            row => row.Action == SpreadsheetImportOperationAction.Created || row.Action == SpreadsheetImportOperationAction.Error);
        await using var verify = CreateContext(connection);
        var acts = await verify.Acts.OrderBy(act => act.Id).ToListAsync();
        Assert.Equal(2, acts.Count);
        Assert.NotEqual(acts[0].ClientId, acts[1].ClientId);
        Assert.Equal(new[] { 100m, 200m }, acts.Select(act => act.Amount));
    }

    [Fact]
    public async Task ImportOwnership_ConcurrentPostgreSqlImportsDoNotCreateDuplicateDocuments()
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres: true);
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        var factory = new CompanyHistoryContextFactory(fixture.Db, postgres: true, accessor);
        var service = new SpreadsheetImportService(factory, accessor, Microsoft.Extensions.Logging.Abstractions.NullLogger<SpreadsheetImportService>.Instance);
        var files = new[] { CreateInvoiceFile(
            new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "CONCURRENT-OWN", "100", "Concurrent owner", "Ожидает" }) };
        var selection = SelectOwnershipRows(await service.PreviewAsync(files));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task<bool> Apply()
        {
            try { return (await service.ApplyAsync(files, selection, deadline.Token)).Status == SpreadsheetImportBatchStatus.Applied; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("пригодных", StringComparison.Ordinal)) { return false; }
            catch (Exception ex) when (IsSerializationFailure(ex)) { return false; }
        }
        var outcomes = await Task.WhenAll(Apply(), Apply());
        Assert.Contains(true, outcomes);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(1, await verify.Payments.CountAsync());
        Assert.Equal(1, await verify.Invoices.CountAsync());
        Assert.Equal(1, await verify.Clients.CountAsync());
        Assert.Equal(100m, (await verify.Payments.SingleAsync()).Amount);

        static bool IsSerializationFailure(Exception error)
            => error is Npgsql.PostgresException { SqlState: "40001" }
                || (error.InnerException is not null && IsSerializationFailure(error.InnerException));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task ImportOwnership_RollbackDoesNotRemoveInvoiceWithNewReferences(bool foreignOwner, bool actReference, bool archivedAct)
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var files = new[] { CreateInvoiceFile(
            new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "ROLLBACK-OWN", "100", "First owner", "Ожидает" }) };
        var batch = await service.ApplyAsync(files, SelectOwnershipRows(await service.PreviewAsync(files)));
        int documentId;
        int invoiceId;
        await using (var db = CreateContext(connection))
        {
            var document = await db.Payments.Include(p => p.Client).Include(p => p.Invoice).SingleAsync();
            documentId = document.Id;
            invoiceId = document.InvoiceId!.Value;
            if (actReference)
                db.Acts.Add(new Act
                {
                    Number = "EXTERNAL-ACT",
                    ClientId = document.ClientId,
                    InvoiceId = document.Id,
                    Date = document.Date,
                    Amount = 30
                });
            else
            {
                var child = OwnershipImportPayment(foreignOwner ? new Client { Name = "Another owner" } : document.Client!, document.Invoice!, 30);
                child.DocumentKind = DocumentKind.Payment;
                db.Payments.Add(child);
            }
            await db.SaveChangesAsync();
            if (archivedAct)
            {
                db.Acts.Remove(await db.Acts.SingleAsync());
                await db.SaveChangesAsync();
            }
        }
        var rollback = await service.RollbackAsync(batch.Id);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, rollback.Status);
        await using var verify = CreateContext(connection);
        Assert.Equal(actReference ? 1 : 2, await verify.Payments.CountAsync());
        if (actReference) Assert.Equal(documentId, (await verify.Acts.IgnoreQueryFilters().SingleAsync()).InvoiceId);
        var persisted = await verify.Payments.SingleAsync(p => p.Id == documentId);
        Assert.Equal(invoiceId, persisted.InvoiceId);
        Assert.Equal(100m, persisted.Amount);
    }

    [Fact]
    public async Task ImportOwnership_DuplicateCounterpartyNamesRequireAnUnambiguousInn()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        await using (var db = CreateContext(connection))
        {
            db.Clients.AddRange(new Client { Name = "Same name" }, new Client { Name = "Same name" });
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewAsync(new[] { CreateInvoiceFile(
            new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "OWN", "100", "Same name", "Ожидает" }) });
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "invoices").Rows);
        Assert.False(row.IsSelectable);
        Assert.Equal(SpreadsheetImportOperationAction.Error, row.Action);
    }

    [Fact]
    public async Task ImportOwnership_SameNumberAndDateForDifferentClientsRemainIndependentOnRepeatAndRollback()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var files = new[] { CreateInvoiceFile(
            new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "SAME-1", "100", "First owner", "Ожидает" },
            new[] { "01.06.2026", "SAME-1", "200", "Second owner", "Ожидает" }) };

        var preview = await service.PreviewAsync(files);
        Assert.Equal(2, Assert.Single(preview.Sections, section => section.Key == "invoices").SelectableRows);
        var batch = await service.ApplyAsync(files, SelectOwnershipRows(preview));
        Assert.Equal(SpreadsheetImportBatchStatus.Applied, batch.Status);
        await using (var db = CreateContext(connection))
        {
            var documents = await db.Payments.OrderBy(p => p.Id).ToListAsync();
            Assert.Equal(2, documents.Count);
            Assert.NotEqual(documents[0].InvoiceId, documents[1].InvoiceId);
            Assert.NotEqual(documents[0].ClientId, documents[1].ClientId);
            Assert.Equal(new[] { 100m, 200m }, documents.Select(p => p.Amount));
        }

        var repeated = await service.PreviewAsync(files);
        Assert.DoesNotContain(Assert.Single(repeated.Sections, section => section.Key == "invoices").Rows,
            row => row.Action == SpreadsheetImportOperationAction.Created || row.Action == SpreadsheetImportOperationAction.Error);
        var repeatedRows = SelectOwnershipRows(repeated);
        if (repeatedRows.SelectedPreviewIds.Count > 0)
        {
            repeatedRows.AllowUpdates = true;
            repeatedRows.AllowClearingExistingValues = true;
            await service.ApplyAsync(files, repeatedRows);
        }
        await using (var db = CreateContext(connection))
        {
            Assert.Equal(2, await db.Payments.CountAsync());
            Assert.Equal(2, await db.Invoices.CountAsync());
            Assert.Equal(new[] { 100m, 200m }, await db.Payments.OrderBy(p => p.Id).Select(p => p.Amount).ToArrayAsync());
        }
        var rollback = await service.RollbackAsync(batch.Id);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, rollback.Status);
        await using var verify = CreateContext(connection);
        Assert.Empty(await verify.Payments.ToListAsync());
        Assert.Empty(await verify.Invoices.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportOwnership_AmbiguousOrHistoricallyConflictingDocumentsAreNotUpdated(bool sharedHeader)
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        await using (var db = CreateContext(connection))
        {
            var first = new Client { Name = "First owner" };
            var other = sharedHeader ? new Client { Name = "Second owner" } : first;
            var header = new Invoice { Number = "SAME-1", Date = new DateTime(2026, 6, 1) };
            db.Payments.AddRange(OwnershipImportPayment(first, header, 100),
                OwnershipImportPayment(other, sharedHeader ? header : new Invoice { Number = header.Number, Date = header.Date }, 200));
            await db.SaveChangesAsync();
        }

        var files = new[] { CreateInvoiceFile(
            new[] { "Дата счета", "№ счета", "Сумма", "Контрагент", "Статус" },
            new[] { "01.06.2026", "SAME-1", "999", "First owner", "Ожидает" }) };
        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, s => s.Key == "invoices").Rows);
        Assert.False(row.IsSelectable);
        // Applying an ID from an old/forged preview must not bypass the safety decision.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(files,
            new SpreadsheetImportApplyRequest { SelectedPreviewIds = new() { row.PreviewId }, AllowUpdates = true, AllowClearingExistingValues = true }));
        Assert.Contains("пригодных", error.Message);
        await using var verify = CreateContext(connection);
        Assert.Equal(new[] { 100m, 200m }, await verify.Payments.OrderBy(p => p.Id).Select(p => p.Amount).ToArrayAsync());
        Assert.Equal(sharedHeader ? 1 : 2, await verify.Invoices.CountAsync());
    }

    [Fact]
    public async Task ImportOwnership_ActDoesNotAttachToAnotherClientsInvoiceWithSameNumber()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        await using (var db = CreateContext(connection))
        {
            db.Clients.Add(new Client { Name = "Second owner" });
            db.Payments.Add(OwnershipImportPayment(new Client { Name = "First owner" },
                new Invoice { Number = "SAME-1", Date = new DateTime(2026, 6, 1) }, 100));
            await db.SaveChangesAsync();
        }
        var files = new[] { CreateActFile(
            new[] { "Дата акта", "№ акта", "Сумма", "Контрагент", "Статус", "Счёт №" },
            new[] { "02.06.2026", "ACT-OTHER", "100", "Second owner", "Подписан", "SAME-1" }) };
        var preview = await service.PreviewAsync(files);
        await service.ApplyAsync(files, SelectOwnershipRows(preview));
        await using var verify = CreateContext(connection);
        Assert.DoesNotContain(await verify.Acts.ToListAsync(), act => act.InvoiceId.HasValue);
        Assert.Equal(100m, (await verify.Payments.SingleAsync()).Amount);
    }

    private static Payment OwnershipImportPayment(Client client, Invoice invoice, decimal amount)
        => new()
        {
            Client = client,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Date = invoice.Date,
            PlannedDate = invoice.Date,
            Amount = amount,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Description = "Ownership boundary fixture",
            Status = PaymentStatus.Pending,
        };

    private static SpreadsheetImportApplyRequest SelectOwnershipRows(PayPlanner.Api.Models.Responses.SpreadsheetImportPreviewDto preview)
        => new() { SelectedPreviewIds = preview.Sections.SelectMany(s => s.Rows).Where(r => r.IsSelectable).Select(r => r.PreviewId).ToList() };
}
