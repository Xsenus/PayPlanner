using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ClientsControllerInternalCoverageTests
{
    [Fact]
    public void DeserializeClientProfile_ReturnsNull_ForBrokenJson()
    {
        var method = typeof(ClientsController)
            .GetMethod("DeserializeClientProfile", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var genericMethod = method!.MakeGenericMethod(typeof(TestProfile));
        var result = genericMethod.Invoke(null, ["{broken-json"]);

        Assert.Null(result);
    }

    [Fact]
    public void ParseAddressType_RecognizesLegal_AndFallsBackToOther()
    {
        var method = typeof(ClientsController)
            .GetMethod("ParseAddressType", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var legal = Assert.IsType<AddressType>(method!.Invoke(null, ["legal address"]));
        var other = Assert.IsType<AddressType>(method.Invoke(null, ["mailing address"]));

        Assert.Equal(AddressType.Legal, legal);
        Assert.Equal(AddressType.Other, other);
    }

    [Fact]
    public async Task GetStats_PrefersLastPaymentDate_WhenItExists()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin");
        var client = new Client
        {
            Name = "Клиент со статистикой",
            ResponsibleUserId = admin.Id,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                ClientId = client.Id,
                Type = PaymentType.Income,
                Date = new DateTime(2026, 4, 20),
                PlannedDate = new DateTime(2026, 4, 20),
                LastPaymentDate = new DateTime(2026, 4, 15),
                Amount = 1500m,
                PaidAmount = 1500m,
                Status = PaymentStatus.Completed,
                IsPaid = true,
                DocumentKind = DocumentKind.Payment,
                Description = "Оплаченный платёж",
                Notes = string.Empty,
                SystemNotes = string.Empty,
            },
            new Payment
            {
                ClientId = client.Id,
                Type = PaymentType.Income,
                Date = new DateTime(2026, 4, 25),
                PlannedDate = new DateTime(2026, 4, 25),
                Amount = 700m,
                PaidAmount = 0m,
                Status = PaymentStatus.Pending,
                IsPaid = false,
                DocumentKind = DocumentKind.Payment,
                Description = "Ожидаемый платёж",
                Notes = string.Empty,
                SystemNotes = string.Empty,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, admin.Id);
        var action = await controller.GetStats(client.Id, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(new DateTime(2026, 4, 15), stats.LastPaymentDate);
    }

    [Fact]
    public async Task Update_FiltersOutEmailAndPhone_FromMissingRequirements()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin");
        var client = new Client { Name = "До изменения", IsActive = true, ResponsibleUserId = admin.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var controller = CreateController(
            db,
            currentUserId: admin.Id,
            formRequirementsService: new FixedMissingFieldsService(["Email", "Phone", "Name"]));

        var action = await controller.Update(client.Id, new Client { Name = "Тест" }, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(action);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        var missingFields = json.RootElement.GetProperty("missingFields").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["Name"], missingFields);
        Assert.Equal("До изменения", (await db.Clients.AsNoTracking().SingleAsync(item => item.Id == client.Id)).Name);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_MissingActorDoesNotRevealMissingRequirements(bool authenticated)
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, currentUserId: null,
            formRequirementsService: new FixedMissingFieldsService(["Email", "Phone", "Name"]));
        if (!authenticated) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.IsType<UnauthorizedResult>(await controller.Update(123, new Client { Name = "Тест" }, CancellationToken.None));
        Assert.Empty(await db.Clients.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ObjectAuditEvents.AsNoTracking().ToArrayAsync());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task SyncClientAddressesAsync_RemovesStaleLegacyAddresses()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Клиент с адресами",
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.AddressRecords.AddRange(
            new AddressRecord
            {
                ClientId = client.Id,
                Type = AddressType.Registration,
                Value = "Старый адрес регистрации",
                IsPrimary = true,
            },
            new AddressRecord
            {
                ClientId = client.Id,
                Type = AddressType.Actual,
                Value = "Старый фактический адрес",
                IsPrimary = false,
            },
            new AddressRecord
            {
                ClientId = client.Id,
                Type = AddressType.Legal,
                Value = "Старый юридический адрес",
                IsPrimary = false,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUserId: null);
        var payload = JsonSerializer.Serialize(new ClientAddressProfile
        {
            RegistrationAddress = "Новый адрес регистрации",
        });

        await InvokePrivateAsync(controller, "SyncClientAddressesAsync", client, payload);
        await db.SaveChangesAsync();

        var addresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.ClientId == client.Id)
            .OrderBy(item => item.Type)
            .ToListAsync();

        var address = Assert.Single(addresses);
        Assert.Equal(AddressType.Registration, address.Type);
        Assert.Equal("Новый адрес регистрации", address.Value);
    }

    [Fact]
    public async Task SyncClientAddressesAsync_KeepsOnlyOnePrimaryAddress_ForLegacyFields()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Legacy primary client",
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUserId: null);
        var payload = JsonSerializer.Serialize(new ClientAddressProfile
        {
            RegistrationAddress = "Registration address",
            ActualAddress = "Actual address",
        });

        await InvokePrivateAsync(controller, "SyncClientAddressesAsync", client, payload);
        await db.SaveChangesAsync();

        var addresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.ClientId == client.Id)
            .OrderBy(item => item.Type)
            .ToListAsync();

        Assert.Equal(2, addresses.Count);
        Assert.Equal("Registration address", client.Address);
        Assert.True(addresses.Single(item => item.Type == AddressType.Registration).IsPrimary);
        Assert.False(addresses.Single(item => item.Type == AddressType.Actual).IsPrimary);
    }

    [Fact]
    public async Task RemoveClientProfileArtifactsAsync_ReturnsEarly_ForEmptyIds()
    {
        await using var db = await CreateContextAsync();
        // The legacy case now exercises the shared deletion service, where
        // profile cleanup moved. An empty selection must never change a profile.
        var rules = typeof(ClientsController).Assembly.GetType("PayPlanner.Api.Services.Clients.ClientDeletionRules", throwOnError: true)!;
        var remove = rules.GetMethod("RemoveProfileAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(remove);
        await Assert.IsAssignableFrom<Task>(remove.Invoke(null, [db, Array.Empty<int>(), CancellationToken.None]));

        Assert.Equal(0, await db.AddressRecords.CountAsync());
        Assert.Equal(0, await db.ClientPassports.CountAsync());
        var client = new Client { Name = "Сохранённый профиль", IsActive = true };
        var address = new AddressRecord { Client = client, Type = AddressType.Registration, Value = "Сохранённый адрес" };
        var passport = new ClientPassport { Client = client, Number = "654321", IsPrimary = true };
        db.AddRange(client, address, passport);
        await db.SaveChangesAsync();

        await Assert.IsAssignableFrom<Task>(remove.Invoke(null, [db, Array.Empty<int>(), CancellationToken.None]));
        Assert.Equal("Сохранённый адрес", (await db.AddressRecords.AsNoTracking().SingleAsync()).Value);
        Assert.Equal("654321", (await db.ClientPassports.AsNoTracking().SingleAsync()).Number);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public void BuildClientRequirementsModel_MapsPowerOfAttorneyIds()
    {
        var model = new Client
        {
            Name = "Клиент",
            PowersOfAttorney =
            [
                new PowerOfAttorney { Id = 11 },
                new PowerOfAttorney { Id = 29 },
            ],
        };

        var result = Assert.IsType<ClientRequirementsModel>(InvokePrivateStatic("BuildClientRequirementsModel", model, null));

        Assert.Equal([11, 29], result.PowersOfAttorney);
    }

    private static object? InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(ClientsController).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var method = typeof(ClientsController).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(target, args));
        await task;
    }

    private static ClientsController CreateController(
        PaymentContext db,
        int? currentUserId,
        ILegalEntityEnrichmentService? enrichmentService = null,
        IFormRequirementsService? formRequirementsService = null)
    {
        var claims = new List<Claim>();
        if (currentUserId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()));
        }

        return new ClientsController(
            db,
            enrichmentService ?? new StubEnrichmentService(),
            formRequirementsService ?? new FixedMissingFieldsService([]))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Name = roleName,
                Description = roleName,
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"clients-internal-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Тестовый пользователь",
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

    private sealed class TestProfile
    {
        public string? Name { get; init; }
    }

    private sealed class FixedMissingFieldsService(IReadOnlyCollection<string> missingFields) : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(missingFields.ToList());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(missingFields.ToList());
    }

    private sealed class StubEnrichmentService : ILegalEntityEnrichmentService
    {
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(
                Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(
            string? query,
            string? inn,
            int limit,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }
}
