using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentContextAuditTests
{
    [Fact]
    public async Task SaveChanges_FillsCreatedAndUpdatedAuditMetadata()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var client = new Client
        {
            Name = "Audit client",
            Phone = "79990000000"
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        Assert.Equal(42, db.Entry(client).Property<int?>("CreatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(client).Property<string?>("CreatedByUserName").CurrentValue);
        Assert.Equal(42, db.Entry(client).Property<int?>("UpdatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(client).Property<string?>("UpdatedByUserName").CurrentValue);
        Assert.NotEqual(default, db.Entry(client).Property<DateTime>("CreatedAt").CurrentValue);
        Assert.NotNull(db.Entry(client).Property<DateTime?>("UpdatedAt").CurrentValue);
        Assert.False(db.Entry(client).Property<bool>("IsDeleted").CurrentValue);
    }

    [Fact]
    public async Task SaveChanges_FillsCreatedAndUpdatedAuditMetadata_ForPayment()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var payment = new Payment
        {
            Date = new DateTime(2026, 4, 8),
            PlannedDate = new DateTime(2026, 4, 8),
            Amount = 1500m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Description = "Audit payment"
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        Assert.Equal(42, db.Entry(payment).Property<int?>("CreatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(payment).Property<string?>("CreatedByUserName").CurrentValue);
        Assert.Equal(42, db.Entry(payment).Property<int?>("UpdatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(payment).Property<string?>("UpdatedByUserName").CurrentValue);
        Assert.NotEqual(default, db.Entry(payment).Property<DateTime>("CreatedAt").CurrentValue);
        Assert.NotNull(db.Entry(payment).Property<DateTime?>("UpdatedAt").CurrentValue);
        Assert.False(db.Entry(payment).Property<bool>("IsDeleted").CurrentValue);
    }

    [Fact]
    public async Task SaveChanges_FillsCreatedAndUpdatedAuditMetadata_ForUserAndRole()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var role = new Role
        {
            Name = "auditor",
            Description = "Audit role",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        Assert.Equal(42, db.Entry(role).Property<int?>("CreatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(role).Property<string?>("CreatedByUserName").CurrentValue);
        Assert.Equal(42, db.Entry(role).Property<int?>("UpdatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(role).Property<string?>("UpdatedByUserName").CurrentValue);
        Assert.Null(db.Entry(role).Metadata.FindProperty("IsDeleted"));

        role.Description = "Audit role updated";
        await db.SaveChangesAsync();

        Assert.NotNull(db.Entry(role).Property<DateTime?>("UpdatedAt").CurrentValue);

        var user = new User
        {
            Email = "audit-user@example.com",
            PasswordHash = "hashed-password",
            FullName = "Audit User",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(42, db.Entry(user).Property<int?>("CreatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(user).Property<string?>("CreatedByUserName").CurrentValue);
        Assert.Equal(42, db.Entry(user).Property<int?>("UpdatedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(user).Property<string?>("UpdatedByUserName").CurrentValue);
        Assert.Null(db.Entry(user).Metadata.FindProperty("IsDeleted"));
    }

    [Fact]
    public async Task SaveChanges_WritesObjectAuditEvents_ForCreateUpdateAndDelete()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var client = new Client
        {
            Name = "History client",
            Phone = "79990000003"
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var createdEvent = await db.ObjectAuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.ObjectType == "client" && item.ObjectId == client.Id && item.Action == "Created");

        Assert.Equal("Audit Tester", createdEvent.ActorUserName);
        Assert.Equal("Объект создан", createdEvent.Description);

        client.Phone = "79990000004";
        await db.SaveChangesAsync();

        var updatedEvent = await db.ObjectAuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.ObjectType == "client" && item.ObjectId == client.Id && item.Action == "Updated");

        Assert.Equal("Audit Tester", updatedEvent.ActorUserName);
        Assert.NotNull(updatedEvent.MetadataJson);

        using var metadataDocument = JsonDocument.Parse(updatedEvent.MetadataJson!);
        Assert.True(metadataDocument.RootElement.TryGetProperty("changedFields", out var changedFieldsElement));
        Assert.Contains(
            changedFieldsElement.EnumerateArray().Select(item => item.GetString()),
            item => item == "Phone");

        Assert.True(metadataDocument.RootElement.TryGetProperty("changes", out var changesElement));
        var phoneChange = changesElement.EnumerateArray()
            .FirstOrDefault(item => item.TryGetProperty("field", out var field) && field.GetString() == "Phone");

        Assert.Equal("[СКРЫТО]", phoneChange.GetProperty("oldValue").GetString());
        Assert.Equal("[СКРЫТО]", phoneChange.GetProperty("newValue").GetString());
        Assert.DoesNotContain("79990000003", updatedEvent.MetadataJson);
        Assert.DoesNotContain("79990000004", updatedEvent.MetadataJson);

        db.Clients.Remove(client);
        await db.SaveChangesAsync();

        var deletedEvent = await db.ObjectAuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.ObjectType == "client" && item.ObjectId == client.Id && item.Action == "Deleted");

        Assert.Equal("Audit Tester", deletedEvent.ActorUserName);
        Assert.Equal("Объект помечен удаленным", deletedEvent.Description);
    }

    [Fact]
    public async Task Remove_SoftDeletesAuditedEntity_AndHidesItFromRegularQueries()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var client = new Client
        {
            Name = "Soft delete client",
            Phone = "79990000001"
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Clients.Remove(client);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Clients.ToListAsync());

        var deletedClient = await db.Clients.IgnoreQueryFilters().SingleAsync();
        Assert.True(db.Entry(deletedClient).Property<bool>("IsDeleted").CurrentValue);
        Assert.Equal(42, db.Entry(deletedClient).Property<int?>("DeletedByUserId").CurrentValue);
        Assert.Equal("Audit Tester", db.Entry(deletedClient).Property<string?>("DeletedByUserName").CurrentValue);
        Assert.NotNull(db.Entry(deletedClient).Property<DateTime?>("DeletedAt").CurrentValue);
    }

    [Fact]
    public async Task Restore_ClearsDeletedAuditFields_AndWritesRestoredEvent()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var client = new Client
        {
            Name = "Restore client",
            Phone = "79990000005"
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Clients.Remove(client);
        await db.SaveChangesAsync();

        var deletedClient = await db.Clients.IgnoreQueryFilters().SingleAsync(item => item.Id == client.Id);
        db.Entry(deletedClient).Property<bool>("IsDeleted").CurrentValue = false;
        db.Entry(deletedClient).Property<DateTime?>("DeletedAt").CurrentValue = null;
        db.Entry(deletedClient).Property<int?>("DeletedByUserId").CurrentValue = null;
        db.Entry(deletedClient).Property<string?>("DeletedByUserName").CurrentValue = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var restoredClient = await db.Clients.SingleAsync(item => item.Id == client.Id);
        Assert.False(db.Entry(restoredClient).Property<bool>("IsDeleted").CurrentValue);
        Assert.Null(db.Entry(restoredClient).Property<DateTime?>("DeletedAt").CurrentValue);
        Assert.Null(db.Entry(restoredClient).Property<int?>("DeletedByUserId").CurrentValue);
        Assert.Null(db.Entry(restoredClient).Property<string?>("DeletedByUserName").CurrentValue);

        var restoredEvent = await db.ObjectAuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.ObjectType == "client" && item.ObjectId == client.Id && item.Action == "Restored");

        Assert.Equal("Audit Tester", restoredEvent.ActorUserName);
        Assert.Equal("Объект восстановлен", restoredEvent.Description);
    }

    [Fact]
    public async Task Delete_MetadataOnlyAuditedRole_WritesDeletedEvent_AndRemovesRow()
    {
        await using var db = await CreateContextAsync(CreateHttpContextAccessor());

        var role = new Role
        {
            Name = "temporary-role",
            Description = "Temporary role",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.Roles.Remove(role);
        await db.SaveChangesAsync();

        Assert.False(await db.Roles.AnyAsync(item => item.Id == role.Id));

        var deletedEvent = await db.ObjectAuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.ObjectType == "role" && item.ObjectId == role.Id && item.Action == "Deleted");

        Assert.Equal("Audit Tester", deletedEvent.ActorUserName);
        Assert.Equal("Объект удален", deletedEvent.Description);
    }

    [Fact]
    public async Task SourceImportContext_CanBuildModelWithoutAuditShadowColumns()
    {
        await using var db = await CreateContextAsync(httpContextAccessor: null, useObjectAuditModel: false);

        var clientEntity = db.Model.FindEntityType(typeof(Client));

        Assert.NotNull(clientEntity);
        Assert.Null(clientEntity!.FindProperty("IsDeleted"));
        Assert.Null(clientEntity.FindProperty("CreatedByUserId"));
    }

    [Fact]
    public async Task SourceImportContext_RemovesEntityWithoutSoftDeleteAudit()
    {
        await using var db = await CreateContextAsync(httpContextAccessor: null, useObjectAuditModel: false);

        var client = new Client
        {
            Name = "Legacy source client",
            Phone = "79990000002"
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Clients.Remove(client);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Clients.ToListAsync());
    }

    [Fact]
    public async Task DbContextFactory_ResolvesAuditAwareContext()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddDbContextFactory<PaymentContext>(options => options.UseSqlite("Data Source=:memory:"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PaymentContext>>();

        await using var db = await factory.CreateDbContextAsync();

        Assert.NotNull(db.Model.FindEntityType(typeof(Client))?.FindProperty("IsDeleted"));
    }

    private static async Task<PaymentContext> CreateContextAsync(
        IHttpContextAccessor? httpContextAccessor = null,
        bool useObjectAuditModel = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options, httpContextAccessor, useObjectAuditModel);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static IHttpContextAccessor CreateHttpContextAccessor()
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim(ClaimTypes.Name, "Audit Tester"),
            },
            authenticationType: "TestAuth");

        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }
}
