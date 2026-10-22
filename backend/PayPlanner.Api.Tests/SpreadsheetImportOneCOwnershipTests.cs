using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceOneCXmlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportOwnership_UniqueUidOutranksAnUnidentifiedNumberCollisionAndCannotChangeOwner(bool foreignOwner)
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = BuildOneCSingleInvoiceXml("ORIGINAL-UID", "2022-01-20T00:00:00", "100.00");
        await ApplyAllSelectableRowsAsync(service, new[] { new SpreadsheetImportUploadFile("original.xml", System.Text.Encoding.UTF8.GetBytes(xml)) });
        int originalId;
        int collisionId;
        await using (var db = CreateContext(connection))
        {
            var original = await db.Payments.SingleAsync();
            originalId = original.Id;
            var owner = foreignOwner ? new Client { Name = "Other owner" } : await db.Clients.SingleAsync();
            var collision = new Payment
            {
                Client = owner,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Invoice = new Invoice { Number = "RENAMED-UID", Date = new DateTime(2022, 1, 20) },
                Account = "RENAMED-UID",
                AccountDate = new DateTime(2022, 1, 20),
                Date = new DateTime(2022, 1, 20),
                Amount = 200,
                Description = "Independent unidentified document",
                Status = PaymentStatus.Pending
            };
            db.Payments.Add(collision);
            await db.SaveChangesAsync();
            collisionId = collision.Id;
        }
        var changed = xml.Replace("ORIGINAL-UID", "RENAMED-UID", StringComparison.Ordinal);
        if (foreignOwner) changed = changed.Replace("ООО Ромашка", "Other owner", StringComparison.Ordinal)
            .Replace("1234567890", "9876543210", StringComparison.Ordinal);
        var files = new[] { new SpreadsheetImportUploadFile("renamed.xml", System.Text.Encoding.UTF8.GetBytes(changed)) };
        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "invoices").Rows);
        if (foreignOwner)
        {
            Assert.False(row.IsSelectable);
            Assert.Equal(SpreadsheetImportOperationAction.Error, row.Action);
        }
        else
        {
            Assert.True(row.IsSelectable);
            var batch = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
            { SelectedPreviewIds = new() { row.PreviewId }, AllowUpdates = true, AllowClearingExistingValues = true });
            Assert.Equal(originalId, Assert.Single(batch.Operations, operation => operation.SectionKey == "invoices").EntityId);
        }
        await using var verify = CreateContext(connection);
        Assert.Equal(200m, (await verify.Payments.SingleAsync(payment => payment.Id == collisionId)).Amount);
        Assert.Equal(100m, (await verify.Payments.SingleAsync(payment => payment.Id == originalId)).Amount);
        Assert.Equal(2, await verify.Payments.CountAsync());
        Assert.Equal(2, await verify.Invoices.CountAsync());
    }

    [Fact]
    public async Task ImportOwnership_OneCDifferentUidsPreserveSameNumberDocumentsOnRepeat()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = AppendOneCObjects(BuildOneCSingleInvoiceXml("SAME-UID", "2022-01-20T00:00:00", "100.00"),
            BuildRepeatedOneCInvoiceObject("SAME-UID", "2022-01-20T00:00:00", "200.00")
                .Replace("INV-UID-45", "DISTINCT-SECOND-UID", StringComparison.Ordinal));
        var files = new[] { new SpreadsheetImportUploadFile("ownership.xml", System.Text.Encoding.UTF8.GetBytes(xml)) };
        var preview = await service.PreviewAsync(files);
        Assert.Equal(2, Assert.Single(preview.Sections, section => section.Key == "invoices").SelectableRows);
        await ApplyAllSelectableRowsAsync(service, files);
        var repeated = await service.PreviewAsync(files);
        Assert.DoesNotContain(Assert.Single(repeated.Sections, section => section.Key == "invoices").Rows,
            row => row.Action == SpreadsheetImportOperationAction.Created || row.Action == SpreadsheetImportOperationAction.Error);
        await using var db = CreateContext(connection);
        var payments = await db.Payments.ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(2, payments.Select(payment => payment.InvoiceId).Distinct().Count());
        Assert.Equal(new[] { 100m, 200m }, payments.Select(payment => payment.Amount).OrderBy(amount => amount));
    }

    [Fact]
    public async Task ImportOwnership_OneCUidCannotTransferDocumentToAnotherCounterparty()
    {
        await using var connection = await CreateOpenConnectionAsync();
        var service = await CreateServiceAsync(connection);
        var xml = BuildOneCSingleInvoiceXml("OWN-UID", "2022-01-20T00:00:00", "100.00");
        await ApplyAllSelectableRowsAsync(service, new[] { new SpreadsheetImportUploadFile("initial.xml", System.Text.Encoding.UTF8.GetBytes(xml)) });
        var changed = xml.Replace("ООО Ромашка", "Other owner", StringComparison.Ordinal)
            .Replace("1234567890", "9876543210", StringComparison.Ordinal).Replace("100.00", "999.00", StringComparison.Ordinal);
        var files = new[] { new SpreadsheetImportUploadFile("foreign.xml", System.Text.Encoding.UTF8.GetBytes(changed)) };
        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "invoices").Rows);
        Assert.False(row.IsSelectable);
        Assert.Equal(SpreadsheetImportOperationAction.Error, row.Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(files,
            new SpreadsheetImportApplyRequest { SelectedPreviewIds = new() { row.PreviewId }, AllowUpdates = true }));
        await using var db = CreateContext(connection);
        Assert.Equal(100m, (await db.Payments.SingleAsync()).Amount);
        Assert.Equal(1, await db.Invoices.CountAsync());
    }
}
