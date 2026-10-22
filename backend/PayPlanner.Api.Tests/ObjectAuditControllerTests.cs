using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ObjectAuditControllerTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_object_audit_test_";

    [Fact]
    public async Task GetEvents_ReturnsFilteredPagedFeed_WhenUserHasAuditAccess()
    {
        await using var db = await CreateContextAsync();

        var role = new Role
        {
            Name = "admin",
            Description = "Administrator",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "audit@example.com",
            PasswordHash = "hash",
            FullName = "Иван Аудитор",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.ObjectAuditEvents.AddRange(
            new ObjectAuditEvent
            {
                ObjectType = "client",
                ObjectId = 11,
                Action = "Updated",
                Description = "Пользователь изменил клиента",
                MetadataJson = "{\"changedFields\":[\"PhoneNumber\"]}",
                ActorUserId = user.Id,
                ActorUserName = "Иван Аудитор",
                CreatedAt = new DateTime(2026, 4, 9, 10, 30, 0, DateTimeKind.Utc),
            },
            new ObjectAuditEvent
            {
                ObjectType = "client",
                ObjectId = 12,
                Action = "Created",
                Description = "Объект создан",
                ActorUserId = user.Id,
                ActorUserName = "Иван Аудитор",
                CreatedAt = new DateTime(2026, 4, 8, 8, 0, 0, DateTimeKind.Utc),
            },
            new ObjectAuditEvent
            {
                ObjectType = "payment",
                ObjectId = 33,
                Action = "Updated",
                Description = "Изменено поле: Сумма",
                ActorUserId = user.Id,
                ActorUserName = "Мария Тестова",
                CreatedAt = new DateTime(2026, 4, 9, 9, 0, 0, DateTimeKind.Utc),
            });

        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id, user.FullName);

        var result = await controller.GetEvents(
            search: "телефон",
            objectType: "client",
            action: "updated",
            actor: "иван",
            from: null,
            to: null,
            page: 1,
            pageSize: 25,
            searchAliases: ["PhoneNumber"],
            fromUtc: new DateTimeOffset(2026, 4, 8, 21, 0, 0, TimeSpan.Zero),
            toUtcExclusive: new DateTimeOffset(2026, 4, 9, 21, 0, 0, TimeSpan.Zero),
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PagedResponse<ObjectAuditEventDto>>(ok.Value);

        var item = Assert.Single(payload.Items);
        Assert.Equal(1, payload.Total);
        Assert.Equal("client", item.ObjectType);
        Assert.Equal(11, item.ObjectId);
        Assert.Equal("Updated", item.Action);
        Assert.Equal("Иван Аудитор", item.ActorUserName);
    }

    [Fact]
    public async Task GetEvents_ReturnsForbid_WhenUserHasNoAuditAccess()
    {
        await using var db = await CreateContextAsync();
        var controller = new ObjectAuditController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.GetEvents(
            search: null,
            objectType: null,
            action: null,
            actor: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CaseScopedHistory_RequiresVisibleCase()
    {
        await using var db = await CreateContextAsync();

        var role = new Role
        {
            Name = "lawyer",
            Description = "Lawyer",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Audit,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var currentUser = new User
        {
            Email = "lawyer@example.com",
            PasswordHash = "hash",
            FullName = "Юрист",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var otherUser = new User
        {
            Email = "other@example.com",
            PasswordHash = "hash",
            FullName = "Другой юрист",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(currentUser, otherUser);
        await db.SaveChangesAsync();

        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = currentUser.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Чужой клиент",
            IsActive = true,
            ResponsibleUserId = otherUser.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Видимое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = currentUser.Id,
            CreatedAt = DateTime.UtcNow,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            Title = "Чужое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherUser.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var visibleDocument = new ClientCaseDocument
        {
            ClientCaseId = visibleCase.Id,
            OriginalFileName = "visible.pdf",
            StoredFileName = "visible.pdf",
            StoragePath = "visible.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow,
        };
        var hiddenDocument = new ClientCaseDocument
        {
            ClientCaseId = hiddenCase.Id,
            OriginalFileName = "hidden.pdf",
            StoredFileName = "hidden.pdf",
            StoragePath = "hidden.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.AddRange(visibleDocument, hiddenDocument);
        await db.SaveChangesAsync();

        db.ObjectAuditEvents.AddRange(
            new ObjectAuditEvent
            {
                ObjectType = "client-case-document",
                ObjectId = visibleDocument.Id,
                Action = "Created",
                Description = "Видимый документ",
                ActorUserId = currentUser.Id,
                ActorUserName = currentUser.FullName,
                CreatedAt = DateTime.UtcNow,
            },
            new ObjectAuditEvent
            {
                ObjectType = "client-case-document",
                ObjectId = hiddenDocument.Id,
                Action = "Created",
                Description = "Скрытый документ",
                ActorUserId = otherUser.Id,
                ActorUserName = otherUser.FullName,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id, currentUser.FullName);

        var visibleHistoryResult = await controller.GetHistory("client-case-document", visibleDocument.Id, CancellationToken.None);
        var visibleOk = Assert.IsType<OkObjectResult>(visibleHistoryResult.Result);
        var visibleHistory = Assert.IsAssignableFrom<IReadOnlyCollection<ObjectAuditEventDto>>(visibleOk.Value);
        Assert.Single(visibleHistory);

        var hiddenHistoryResult = await controller.GetHistory("client-case-document", hiddenDocument.Id, CancellationToken.None);
        Assert.IsType<ForbidResult>(hiddenHistoryResult.Result);

        var feedResult = await controller.GetEvents(
            search: null,
            objectType: "client-case-document",
            action: null,
            actor: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var feedOk = Assert.IsType<OkObjectResult>(feedResult.Result);
        var feed = Assert.IsType<PagedResponse<ObjectAuditEventDto>>(feedOk.Value);
        var item = Assert.Single(feed.Items);
        Assert.Equal(visibleDocument.Id, item.ObjectId);
    }

    [Fact]
    public async Task RestoreClient_RestoresCompleteProfileAggregate()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminAsync(db);
        var client = new Client
        {
            Name = "Восстанавливаемый клиент",
            Phone = "+7 999 555-01-02",
            IsActive = true,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var currentAddress = new AddressRecord
        {
            ClientId = client.Id,
            Type = AddressType.Registration,
            Value = "Текущий адрес",
            IsPrimary = true,
        };
        var oldAddress = new AddressRecord
        {
            ClientId = client.Id,
            Type = AddressType.Actual,
            Value = "Удалённый ранее адрес",
        };
        var passport = new ClientPassport
        {
            ClientId = client.Id,
            Series = "1234",
            Number = "567890",
            IsPrimary = true,
        };
        db.AddRange(currentAddress, oldAddress, passport);
        await db.SaveChangesAsync();

        db.AddressRecords.Remove(oldAddress);
        await db.SaveChangesAsync();
        db.AddressRecords.Remove(currentAddress);
        db.ClientPassports.Remove(passport);
        db.Clients.Remove(client);
        await db.SaveChangesAsync();

        var controller = CreateController(db, admin.Id, admin.FullName);
        var deletedResult = await controller.GetDeletedObjects("client", "555-01", CancellationToken.None);
        var deletedOk = Assert.IsType<OkObjectResult>(deletedResult.Result);
        var deleted = Assert.IsAssignableFrom<IReadOnlyCollection<DeletedObjectAuditItemDto>>(deletedOk.Value);
        Assert.Equal(client.Id, Assert.Single(deleted).ObjectId);

        var restoreResult = await controller.RestoreObject("client", client.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(restoreResult);
        Assert.Equal(1, await db.Clients.CountAsync(item => item.Id == client.Id));
        Assert.Equal(new[] { "Текущий адрес", "Удалённый ранее адрес" }, await db.AddressRecords
            .Where(item => item.ClientId == client.Id)
            .OrderByDescending(item => item.IsPrimary)
            .Select(item => item.Value)
            .ToListAsync());
        Assert.Equal(1, await db.ClientPassports.CountAsync(item => item.ClientId == client.Id));
    }

    [Fact]
    public async Task RestoreLegalEntity_RestoresAddressesDeletedWithOrganization()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminAsync(db);
        var entity = new LegalEntity
        {
            ShortName = "ООО Восстановление",
            FullName = "Общество с ограниченной ответственностью Восстановление",
        };
        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        var address = new AddressRecord
        {
            LegalEntityId = entity.Id,
            Type = AddressType.Legal,
            Value = "630000, Новосибирск",
            IsPrimary = true,
        };
        db.AddressRecords.Add(address);
        await db.SaveChangesAsync();

        db.AddressRecords.Remove(address);
        db.LegalEntities.Remove(entity);
        await db.SaveChangesAsync();

        var controller = CreateController(db, admin.Id, admin.FullName);
        var result = await controller.RestoreObject("legal-entity", entity.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, await db.LegalEntities.CountAsync(item => item.Id == entity.Id));
        Assert.Equal(1, await db.AddressRecords.CountAsync(item => item.LegalEntityId == entity.Id));
    }

    [Fact]
    public async Task DeletedObjects_RestoreIsDenyByDefaultForReadOnlyAuditor()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "audit-reader",
            Description = "Аудитор",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Audit,
            CanView = true,
            CanEdit = false,
            ViewScope = DataVisibilityScope.All,
        });
        var user = new User
        {
            Email = "audit-reader@example.com",
            PasswordHash = "hash",
            FullName = "Аудитор без восстановления",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var contact = new ExternalContact { FullName = "Удалённый контакт" };
        db.AddRange(user, contact);
        await db.SaveChangesAsync();
        db.ExternalContacts.Remove(contact);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id, user.FullName);
        var listResult = await controller.GetDeletedObjects("contact", null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(listResult.Result);

        var restoreResult = await controller.RestoreObject("contact", contact.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(restoreResult);
        Assert.Equal(0, await db.ExternalContacts.CountAsync(item => item.Id == contact.Id));
    }

    [Fact]
    public async Task PostgreSqlRestoreClientProfileExecutesAgainstFreshDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(PostgreSqlDatabasePrefix, databaseName, StringComparison.Ordinal);
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();
            var admin = await CreateAdminAsync(db);
            var client = new Client { Name = "PostgreSQL восстановление", IsActive = true };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            db.AddRange(
                new AddressRecord
                {
                    ClientId = client.Id,
                    Type = AddressType.Registration,
                    Value = "PostgreSQL адрес",
                    IsPrimary = true,
                },
                new ClientPassport
                {
                    ClientId = client.Id,
                    Series = "5400",
                    Number = "123456",
                    IsPrimary = true,
                });
            await db.SaveChangesAsync();
            db.AddressRecords.RemoveRange(await db.AddressRecords.Where(item => item.ClientId == client.Id).ToListAsync());
            db.ClientPassports.RemoveRange(await db.ClientPassports.Where(item => item.ClientId == client.Id).ToListAsync());
            db.Clients.Remove(client);
            await db.SaveChangesAsync();

            var controller = CreateController(db, admin.Id, admin.FullName);
            Assert.IsType<NoContentResult>(await controller.RestoreObject("client", client.Id, CancellationToken.None));
            Assert.Equal(1, await db.Clients.CountAsync(item => item.Id == client.Id));
            Assert.Equal(1, await db.AddressRecords.CountAsync(item => item.ClientId == client.Id));
            Assert.Equal(1, await db.ClientPassports.CountAsync(item => item.ClientId == client.Id));
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task<User> CreateAdminAsync(PaymentContext db)
    {
        var role = new Role
        {
            Name = "admin",
            Description = "Administrator",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Администратор",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ObjectAuditController CreateController(PaymentContext db, int userId, string userName)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, userName),
            },
            authenticationType: "TestAuth");

        return new ObjectAuditController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                },
            },
        };
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
