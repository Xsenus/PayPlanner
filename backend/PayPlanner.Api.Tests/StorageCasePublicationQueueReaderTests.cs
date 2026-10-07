using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCasePublicationQueueReaderTests
{
    [Fact]
    public async Task PagesEqualDatesWithoutDuplicatesAndDoesNotDependOnMutablePivot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var expected = await db.StorageOperations.Where(item => item.OperationType == StorageOperationTypes.PublishCaseDocument)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Select(item => item.Id).ToArrayAsync();
        var reader = new StorageCasePublicationQueueReader(db);
        var first = await reader.ReadAsync(null, null, 2, CancellationToken.None);
        Assert.Equal(expected.Take(2), first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, item => Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind));
        var pivotId = first.Items[^1].Id;
        var pivot = await db.StorageOperations.SingleAsync(item => item.Id == pivotId);
        pivot.UpdatedAt = DateTime.UtcNow.AddDays(1);
        pivot.Status = StorageOperationStatuses.Succeeded;
        await db.SaveChangesAsync();
        var second = await reader.ReadAsync(null, first.NextCursor, 2, CancellationToken.None);
        Assert.Equal(expected.Skip(2).Take(2), second.Items.Select(item => item.Id));
        db.StorageOperations.Remove(pivot);
        await db.SaveChangesAsync();
        var repeated = await reader.ReadAsync(null, first.NextCursor, 2, CancellationToken.None);
        Assert.Equal(second.Items.Select(item => item.Id), repeated.Items.Select(item => item.Id));
        var last = await reader.ReadAsync(null, second.NextCursor, 2, CancellationToken.None);
        Assert.Equal(expected.Skip(4), last.Items.Select(item => item.Id));
        Assert.Null(last.NextCursor);
        var json = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("sensitive-payload", json);
        Assert.DoesNotContain("sensitive-provider-error", json);
        Assert.DoesNotContain("sensitive-worker", json);
    }

    [Fact]
    public async Task FilteredCursorCannotBeReusedForAnotherFilterAndMatchesRetryLeaseRules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var reader = new StorageCasePublicationQueueReader(db);
        var filtered = await reader.ReadAsync(StorageOperationStatuses.DeadLetter, null, 1, CancellationToken.None);
        Assert.NotNull(filtered.NextCursor);
        Assert.All(filtered.Items, item => Assert.Equal(StorageOperationStatuses.DeadLetter, item.Status));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(null, filtered.NextCursor, 1, CancellationToken.None));
        var all = await reader.ReadAsync(null, null, 50, CancellationToken.None);
        var operations = await db.StorageOperations.AsNoTracking().ToDictionaryAsync(item => item.Id);
        foreach (var item in all.Items)
        {
            var operation = operations[item.Id];
            Assert.Equal(operation.Status != StorageOperationStatuses.Running
                && !(operation.LeaseOwner is not null && operation.LeaseExpiresAt is null)
                && !(operation.LeaseExpiresAt > DateTime.UtcNow), item.CanRetry);
        }
        Assert.Equal(6, all.Items.Count);
        Assert.Equal(7, await db.StorageOperations.CountAsync());
    }

    [Fact]
    public async Task AdminRouteDisablesCachingAndRejectsInvalidInput()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var controller = new StorageAdminController(null!, null!, null!, null!, null!, null!, null!, db, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = await controller.GetCasePublicationOperations(cancellationToken: CancellationToken.None);
        Assert.IsType<StorageCasePublicationPage>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<BadRequestObjectResult>((await controller.GetCasePublicationOperations(status: "invalid")).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCasePublicationOperations(after: "broken")).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCasePublicationOperations(limit: 0)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCasePublicationOperations(limit: 201)).Result);
        Assert.Contains(typeof(StorageAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>(), attribute => attribute.Policy == "Admin");
    }

    private static async Task SeedAsync(PaymentContext db)
    {
        var storageObject = new StorageObject
        {
            DataClass = "case-document",
            OwnerType = "case-document",
            OwnerId = "synthetic-case",
            LogicalName = "synthetic.pdf",
            ContentType = "application/pdf"
        };
        db.StorageObjects.Add(storageObject);
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 7; i++)
            db.StorageOperations.Add(new StorageOperation
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                StorageObject = storageObject,
                OperationType = i == 7 ? StorageOperationTypes.Replicate : StorageOperationTypes.PublishCaseDocument,
                Status = i <= 3 ? StorageOperationStatuses.DeadLetter : i == 4 ? StorageOperationStatuses.Running : StorageOperationStatuses.Pending,
                IdempotencyKey = $"synthetic-queue:{i}",
                CreatedAt = date,
                UpdatedAt = date,
                LeaseOwner = i is 2 or 3 ? "sensitive-worker" : null,
                LeaseExpiresAt = i == 2 ? date.AddYears(1) : null,
                PayloadJson = "sensitive-payload",
                LastError = "sensitive-provider-error",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
