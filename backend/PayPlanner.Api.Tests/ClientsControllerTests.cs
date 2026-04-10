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

public class ClientsControllerTests
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";

    [Fact]
    public async Task Create_RoundTripsStructuredAddressEntries_WithFullAddress()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId);

        const string fullAddress = "633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19";
        var model = new Client
        {
            Name = "Тестовый клиент",
            Address = fullAddress,
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = fullAddress,
                        fullAddress,
                        postalCode = "633208",
                        country = "Россия",
                        region = "Новосибирская обл",
                        district = "Искитимский р-н",
                        city = "г Искитим",
                        settlement = "Южный мкр",
                        street = "Южный мкр",
                        house = "37",
                        flat = "19",
                        fiasId = "fias-iskitim",
                        kladrId = "kladr-iskitim",
                        geoLat = "54.6265",
                        geoLon = "83.3045",
                    },
                },
            }),
            IsActive = true,
        };

        var action = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);
        var address = Assert.Single(dto.Addresses);

        Assert.Equal(fullAddress, address.UnrestrictedValue);
        Assert.Equal("Россия", address.Country);
        Assert.Equal("Новосибирская обл", address.RegionWithType);
        Assert.Equal("Искитимский р-н", address.AreaWithType);
        Assert.Equal("г Искитим", address.CityWithType);
        Assert.Equal("Южный мкр", address.SettlementWithType);
        Assert.Equal("Южный мкр", address.StreetWithType);
        Assert.Equal("37", address.House);
        Assert.Equal("19", address.Flat);

        var stored = await db.AddressRecords.SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal(fullAddress, stored.UnrestrictedValue);
        Assert.Equal("Россия", stored.Country);
        Assert.Equal("Новосибирская обл", stored.RegionWithType);
        Assert.Equal("Искитимский р-н", stored.AreaWithType);
        Assert.Equal("г Искитим", stored.CityWithType);
        Assert.Equal("Южный мкр", stored.SettlementWithType);
        Assert.Equal("Южный мкр", stored.StreetWithType);
        Assert.Equal("37", stored.House);
        Assert.Equal("19", stored.Flat);
    }

    private static async Task<int> CreateUserAsync(PaymentContext db)
    {
        var role = new Role
        {
            Name = "user",
            Description = "User",
            CreatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "client-tests@example.com",
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
        return user.Id;
    }

    private static ClientsController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        var controller = new ClientsController(db, new NoopEnrichmentService(), new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };

        return controller;
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

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class NoopEnrichmentService : ILegalEntityEnrichmentService
    {
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }
}
