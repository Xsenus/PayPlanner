using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DocumentWorkflowStatusTests
{
    [Fact]
    public void RequiresClientSignature_IsRequiredForAllDocumentKinds()
    {
        var seed = Assert.Single(
            DocumentWorkflowStatus.Defaults,
            item => item.Code == DocumentWorkflowStatus.RequiresClientSignature);

        Assert.True(DocumentWorkflowStatus.IsMandatory(DocumentWorkflowStatus.RequiresClientSignature));
        Assert.Contains(DocumentWorkflowStatus.RequiresClientSignature, InvoiceWorkflowStatus.All);
        Assert.Contains(DocumentWorkflowStatus.RequiresClientSignature, ActWorkflowStatus.All);
        Assert.Contains(DocumentWorkflowStatus.RequiresClientSignature, ContractStatus.All);
        Assert.Equal("Требуется подпись клиента", seed.Name);
        Assert.True(seed.UseForInvoices);
        Assert.True(seed.UseForActs);
        Assert.True(seed.UseForContracts);
    }

    [Fact]
    public async Task SeedAsync_RestoresMandatoryClientSignatureStatus()
    {
        await using var db = await CreateContextAsync();
        db.DocumentStatuses.Add(new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.RequiresClientSignature,
            Name = "Ожидает клиента",
            Description = "Старое описание",
            ColorHex = "#F59E0B",
            IsActive = false,
            UseForInvoices = false,
            UseForActs = false,
            UseForContracts = false,
            SortOrder = 999,
        });
        await db.SaveChangesAsync();

        await SeedDataService.SeedAsync(db);

        var status = await db.DocumentStatuses.AsNoTracking()
            .SingleAsync(item => item.Code == DocumentWorkflowStatus.RequiresClientSignature);

        Assert.Equal("Требуется подпись клиента", status.Name);
        Assert.Equal("Документ ожидает подпись клиента", status.Description);
        Assert.True(status.IsActive);
        Assert.True(status.UseForInvoices);
        Assert.True(status.UseForActs);
        Assert.True(status.UseForContracts);
        Assert.Equal(30, status.SortOrder);
    }

    [Fact]
    public async Task UpdateDocumentStatus_KeepsClientSignatureStatusRequired()
    {
        await using var db = await CreateContextAsync();
        var status = await AddRequiredStatusAsync(db);
        var controller = new DictionariesAdminController(db);

        var result = await controller.UpdateDocumentStatus(status.Id, new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.RequiresClientSignature,
            Name = "Ожидает клиента",
            Description = "Можно отключить",
            ColorHex = "#F59E0B",
            IsActive = false,
            UseForInvoices = false,
            UseForActs = false,
            UseForContracts = false,
            SortOrder = 999,
        });

        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var updated = await db.DocumentStatuses.AsNoTracking().SingleAsync(item => item.Id == status.Id);
        Assert.Equal("Требуется подпись клиента", updated.Name);
        Assert.True(updated.IsActive);
        Assert.True(updated.UseForInvoices);
        Assert.True(updated.UseForActs);
        Assert.True(updated.UseForContracts);
        Assert.Equal(30, updated.SortOrder);
    }

    [Fact]
    public async Task ToggleDocumentStatus_DoesNotDisableClientSignatureStatus()
    {
        await using var db = await CreateContextAsync();
        var status = await AddRequiredStatusAsync(db);
        var controller = new DictionariesAdminController(db);

        var result = await controller.ToggleDocumentStatus(status.Id);

        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var updated = await db.DocumentStatuses.AsNoTracking().SingleAsync(item => item.Id == status.Id);
        Assert.True(updated.IsActive);
        Assert.True(updated.UseForInvoices);
        Assert.True(updated.UseForActs);
        Assert.True(updated.UseForContracts);
    }

    [Fact]
    public async Task UpdateDocumentStatus_RemovesIncompatibleSigningScopes()
    {
        await using var db = await CreateContextAsync();
        var status = new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.SignedByClient,
            Name = "Подписан клиентом",
            Description = "Счет подписан клиентом",
            ColorHex = "#10B981",
            IsActive = true,
            UseForInvoices = true,
            UseForActs = false,
            UseForContracts = false,
            SortOrder = 90,
        };
        db.DocumentStatuses.Add(status);
        await db.SaveChangesAsync();

        var controller = new DictionariesAdminController(db);

        var result = await controller.UpdateDocumentStatus(status.Id, new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.SignedByClient,
            Name = "Подписан клиентом",
            Description = "Счет подписан клиентом",
            ColorHex = "#10B981",
            IsActive = true,
            UseForInvoices = true,
            UseForActs = true,
            UseForContracts = true,
            SortOrder = 90,
        });

        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var updated = await db.DocumentStatuses.AsNoTracking().SingleAsync(item => item.Id == status.Id);
        Assert.Equal("Подписан клиентом (счет)", updated.Name);
        Assert.True(updated.UseForInvoices);
        Assert.False(updated.UseForActs);
        Assert.False(updated.UseForContracts);
    }

    [Fact]
    public async Task SeedAsync_RemovesIncompatibleSigningScopes()
    {
        await using var db = await CreateContextAsync();
        db.DocumentStatuses.Add(new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.SignedByClient,
            Name = "Подписан клиентом",
            Description = "Счет подписан клиентом",
            ColorHex = "#10B981",
            IsActive = true,
            UseForInvoices = true,
            UseForActs = true,
            UseForContracts = true,
            SortOrder = 90,
        });
        await db.SaveChangesAsync();

        await SeedDataService.SeedAsync(db);

        var status = await db.DocumentStatuses.AsNoTracking()
            .SingleAsync(item => item.Code == DocumentWorkflowStatus.SignedByClient);
        Assert.Equal("Подписан клиентом (счет)", status.Name);
        Assert.True(status.UseForInvoices);
        Assert.False(status.UseForActs);
        Assert.False(status.UseForContracts);
    }

    private static async Task<DocumentStatusEntity> AddRequiredStatusAsync(PaymentContext db)
    {
        var status = new DocumentStatusEntity
        {
            Code = DocumentWorkflowStatus.RequiresClientSignature,
            Name = "Требуется подпись клиента",
            Description = "Документ ожидает подпись клиента",
            ColorHex = "#F59E0B",
            IsActive = true,
            UseForInvoices = true,
            UseForActs = true,
            UseForContracts = true,
            SortOrder = 30,
        };
        db.DocumentStatuses.Add(status);
        await db.SaveChangesAsync();
        return status;
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
