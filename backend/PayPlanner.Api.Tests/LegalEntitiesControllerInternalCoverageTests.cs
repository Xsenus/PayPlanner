using System.Reflection;
using System.Security.Claims;
using PayPlanner.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class LegalEntitiesControllerInternalCoverageTests
{
    [Fact]
    public void MainCompanySettingsState_StoresCompanyIdsAndDefaultId()
    {
        var stateType = typeof(LegalEntitiesController).GetNestedType("MainCompanySettingsState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var companyIds = new[] { 3, 7, 11 };
        var instance = Activator.CreateInstance(
            stateType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { companyIds, 7 },
            culture: null);

        Assert.NotNull(instance);
        Assert.Equal(companyIds, Assert.IsAssignableFrom<IReadOnlyList<int>>(stateType!.GetProperty("CompanyIds")!.GetValue(instance)));
        Assert.Equal(7, stateType.GetProperty("DefaultCompanyId")!.GetValue(instance));
    }

    [Fact]
    public void ValidateInlineAssetPayload_CoversEachOversizeBranch()
    {
        var singleLimit = GetPrivateConstInt("MaxInlineLegalEntityAssetDataUrlLength");
        var totalLimit = GetPrivateConstInt("MaxInlineLegalEntityAssetsTotalLength");

        var logoError = Assert.IsType<string>(InvokePrivateStatic(
            "ValidateInlineAssetPayload",
            new LegalEntityRequest
            {
                LogoDataUrl = new string('a', singleLimit + 1),
            }));

        var sealError = Assert.IsType<string>(InvokePrivateStatic(
            "ValidateInlineAssetPayload",
            new LegalEntityRequest
            {
                SealDataUrl = new string('b', singleLimit + 1),
            }));

        var signatureError = Assert.IsType<string>(InvokePrivateStatic(
            "ValidateInlineAssetPayload",
            new LegalEntityRequest
            {
                DirectorSignatureDataUrl = new string('c', singleLimit + 1),
            }));

        var totalError = Assert.IsType<string>(InvokePrivateStatic(
            "ValidateInlineAssetPayload",
            new LegalEntityRequest
            {
                LogoDataUrl = new string('a', singleLimit - 1),
                SealDataUrl = new string('b', singleLimit - 1),
                DirectorSignatureDataUrl = new string('c', totalLimit - ((singleLimit - 1) * 2) + 1),
            }));

        Assert.Contains("Логотип", logoError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Печать", sealError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Подпись", signatureError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Суммарный размер", totalError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(InvokePrivateStatic("ValidateInlineAssetPayload", new LegalEntityRequest()));
    }

    [Fact]
    public void CanonicalizationAndSuggestionHelpers_HandleStructuredFallbacks_AndLimitBreaks()
    {
        var blankDictionaryName = Assert.IsType<string>(InvokePrivateStatic("CanonicalizeDictionaryName", "  "));
        Assert.Equal(string.Empty, blankDictionaryName);

        var structuredAddressKey = Assert.IsType<string>(InvokePrivateStatic(
            "BuildAddressIdentityKey",
            new AddressRecord
            {
                Type = AddressType.Other,
                Country = "Россия",
                RegionWithType = "Самарская область",
                CityWithType = "г. Новокуйбышевск",
                StreetWithType = "ул. Миронова",
                House = "9",
                Flat = "1",
            }));

        var ogrnKey = Assert.IsType<string>(InvokePrivateStatic(
            "BuildLegalEntitySuggestionIdentityKey",
            new LegalEntitySuggestion(
                ShortName: "ООО Ромашка",
                FullName: null,
                Inn: null,
                Kpp: null,
                Ogrn: "1234567890123",
                Address: null,
                Phone: null,
                Email: null,
                ManagementName: null)));

        var nameAndAddressKey = Assert.IsType<string>(InvokePrivateStatic(
            "BuildLegalEntitySuggestionIdentityKey",
            new LegalEntitySuggestion(
                ShortName: "ООО Альфа",
                FullName: "ООО Альфа",
                Inn: null,
                Kpp: null,
                Ogrn: null,
                Address: "г. Самара, ул. Победы, д. 1",
                Phone: null,
                Email: null,
                ManagementName: null)));

        var deduplicated = Assert.IsAssignableFrom<IReadOnlyList<LegalEntitySuggestion>>(InvokePrivateStatic(
            "DeduplicateLegalEntitySuggestions",
            new List<LegalEntitySuggestion>
            {
                new(
                    ShortName: "ООО Альфа",
                    FullName: null,
                    Inn: "7700000001",
                    Kpp: null,
                    Ogrn: null,
                    Address: null,
                    Phone: null,
                    Email: null,
                    ManagementName: null),
                new(
                    ShortName: "ООО Альфа дубль",
                    FullName: null,
                    Inn: "7700000001",
                    Kpp: null,
                    Ogrn: null,
                    Address: null,
                    Phone: null,
                    Email: null,
                    ManagementName: null),
                new(
                    ShortName: "ООО Бета",
                    FullName: null,
                    Inn: null,
                    Kpp: null,
                    Ogrn: "1234567890123",
                    Address: null,
                    Phone: null,
                    Email: null,
                    ManagementName: null),
                new(
                    ShortName: "ООО Гамма",
                    FullName: "ООО Гамма",
                    Inn: null,
                    Kpp: null,
                    Ogrn: null,
                    Address: "Москва, ул. Тестовая, 5",
                    Phone: null,
                    Email: null,
                    ManagementName: null),
            },
            2));

        Assert.StartsWith($"{(int)AddressType.Other}|", structuredAddressKey, StringComparison.Ordinal);
        Assert.Contains("новокуйбышевск", structuredAddressKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ogrn:1234567890123", ogrnKey);
        Assert.Contains("альфа", nameAndAddressKey, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("победы", nameAndAddressKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, deduplicated.Count);
        Assert.Equal("ООО Альфа", deduplicated[0].ShortName);
        Assert.Equal("ООО Бета", deduplicated[1].ShortName);
    }

    [Fact]
    public void ResolveUpdatedAssetValue_SetProperty_SetIfDifferent_AndFillIfEmpty_HandleEdgeCases()
    {
        var changedValue = InvokePrivateStatic("ResolveUpdatedAssetValue", "current", " next ", true);
        var incomingFallbackValue = InvokePrivateStatic("ResolveUpdatedAssetValue", "current", " newer ", null);
        var currentFallbackValue = InvokePrivateStatic("ResolveUpdatedAssetValue", "current", null, false);

        Assert.Equal("next", changedValue);
        Assert.Equal("newer", incomingFallbackValue);
        Assert.Equal("current", currentFallbackValue);

        var holder = new MutableHolder();
        InvokePrivateStatic("SetProperty", holder, "Name", "Updated");
        InvokePrivateStatic("SetProperty", holder, "MissingProperty", "Ignored");
        InvokePrivateStatic("SetProperty", new ReadOnlyHolder(), "Name", "Ignored");

        Assert.Equal("Updated", holder.Name);

        string? differentTarget = "same";
        var differentFalse = Assert.IsType<bool>(InvokePrivateStatic(
            "SetIfDifferent",
            "same",
            (Action<string?>)(value => differentTarget = value),
            " same "));
        var differentTrue = Assert.IsType<bool>(InvokePrivateStatic(
            "SetIfDifferent",
            "same",
            (Action<string?>)(value => differentTarget = value),
            "other"));

        string? emptyTarget = "filled";
        var fillFalseBecauseCurrentExists = Assert.IsType<bool>(InvokePrivateStatic(
            "FillIfEmpty",
            "filled",
            (Action<string?>)(value => emptyTarget = value),
            "next"));
        emptyTarget = null;
        var fillFalseBecauseIncomingBlank = Assert.IsType<bool>(InvokePrivateStatic(
            "FillIfEmpty",
            null,
            (Action<string?>)(value => emptyTarget = value),
            "   "));
        var fillTrue = Assert.IsType<bool>(InvokePrivateStatic(
            "FillIfEmpty",
            null,
            (Action<string?>)(value => emptyTarget = value),
            " final "));

        Assert.False(differentFalse);
        Assert.True(differentTrue);
        Assert.Equal("other", differentTarget);
        Assert.False(fillFalseBecauseCurrentExists);
        Assert.False(fillFalseBecauseIncomingBlank);
        Assert.True(fillTrue);
        Assert.Equal("final", emptyTarget);
    }

    [Fact]
    public void BuildMigrationDetail_AndApplyCleanedData_CoverOverwriteAndFillBranches()
    {
        var clientDetail = Assert.IsType<LegalEntityAddressMigrationDetailDto>(InvokePrivateStatic(
            "BuildMigrationDetail",
            "CLIENT",
            15,
            "Клиент Тест",
            new LegalEntityAddressMigrationDetailDto
            {
                Action = "Updated",
            }));

        var legalDetail = Assert.IsType<LegalEntityAddressMigrationDetailDto>(InvokePrivateStatic(
            "BuildMigrationDetail",
            "vendor",
            27,
            "Юрлицо Тест",
            new LegalEntityAddressMigrationDetailDto
            {
                Action = "Added",
            }));

        var overwriteTarget = new AddressRecord
        {
            Value = "старое значение",
            UnrestrictedValue = "старый адрес",
            PostalCode = "111111",
            Country = "Россия",
            RegionWithType = "Старая область",
            AreaWithType = "Старый район",
            CityWithType = "Старый город",
            SettlementWithType = "Старый посёлок",
            StreetWithType = "Старая улица",
            House = "1",
            Flat = "1",
            FiasId = "old-fias",
            KladrId = "old-kladr",
            GeoLat = "1",
            GeoLon = "1",
        };

        var cleaned = CreateCleanedAddress();
        var overwriteChanged = Assert.IsType<bool>(InvokePrivateStatic(
            "ApplyCleanedData",
            overwriteTarget,
            "Легаси адрес",
            "Нормализованный адрес",
            cleaned,
            true));

        var fillTarget = new AddressRecord
        {
            Value = string.Empty,
            UnrestrictedValue = string.Empty,
        };

        var fillChanged = Assert.IsType<bool>(InvokePrivateStatic(
            "ApplyCleanedData",
            fillTarget,
            "Исходный адрес",
            "Нормализованный адрес",
            cleaned,
            false));

        Assert.Equal("client", clientDetail.OwnerType);
        Assert.Equal(15, clientDetail.ClientId);
        Assert.Null(clientDetail.LegalEntityId);
        Assert.Equal("legal", legalDetail.OwnerType);
        Assert.Equal(27, legalDetail.LegalEntityId);
        Assert.Null(legalDetail.ClientId);

        Assert.True(overwriteChanged);
        Assert.Equal("Нормализованный адрес", overwriteTarget.Value);
        Assert.Equal("Легаси адрес", overwriteTarget.UnrestrictedValue);
        Assert.Equal("445000", overwriteTarget.PostalCode);
        Assert.Equal("Новая область", overwriteTarget.RegionWithType);
        Assert.Equal("new-fias", overwriteTarget.FiasId);
        Assert.Equal("53.1", overwriteTarget.GeoLat);

        Assert.True(fillChanged);
        Assert.Equal("Нормализованный адрес", fillTarget.Value);
        Assert.Equal("Исходный адрес", fillTarget.UnrestrictedValue);
        Assert.Equal("445000", fillTarget.PostalCode);
        Assert.Equal("Россия", fillTarget.Country);
        Assert.Equal("Новая улица", fillTarget.StreetWithType);
        Assert.Equal("10", fillTarget.House);
    }

    [Fact]
    public async Task ApplyLegalEntityAddressesAsync_HandlesEmptyInput_AndAssignsPrimaryWhenMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, new StubEnrichmentService(), currentUserId: 1);
        var entity = new LegalEntity
        {
            ShortName = "Тестовая компания",
            FullName = "Тестовая компания",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();

        await InvokePrivateAsync(
            controller,
            "ApplyLegalEntityAddressesAsync",
            entity,
            new List<LegalEntityAddressRequest>
            {
                new() { Value = "   " },
            },
            CancellationToken.None);

        Assert.Equal(0, await db.AddressRecords.CountAsync(item => item.LegalEntityId == entity.Id));

        await InvokePrivateAsync(
            controller,
            "ApplyLegalEntityAddressesAsync",
            entity,
            new List<LegalEntityAddressRequest>
            {
                new()
                {
                    Type = (int)AddressType.Legal,
                    Value = "г. Самара, ул. Победы, 1",
                    IsPrimary = false,
                },
                new()
                {
                    Type = (int)AddressType.Legal,
                    Value = "г. Самара, ул. Победы, 2",
                    IsPrimary = false,
                },
            },
            CancellationToken.None);
        await db.SaveChangesAsync();

        var addresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.LegalEntityId == entity.Id)
            .OrderBy(item => item.Value)
            .ToListAsync();

        Assert.Equal(2, addresses.Count);
        var primaryAddress = Assert.Single(addresses, item => item.IsPrimary);
        Assert.Equal("г. Самара, ул. Победы, 1", primaryAddress.Value);
    }

    [Fact]
    public async Task UpdateMainCompanySettings_ReturnsSortedMissingIds()
    {
        await using var db = await CreateContextAsync();
        var actor = new User { Email = "settings-admin@example.test", FullName = "Admin", PasswordHash = "hash", Role = new Role { Name = "admin" }, IsActive = true, IsApproved = true };
        db.Users.Add(actor); await db.SaveChangesAsync();
        var controller = CreateController(db, new StubEnrichmentService(), currentUserId: actor.Id);

        var action = await controller.UpdateMainCompanySettings(
            new UpdateMainCompanySettingsRequest
            {
                CompanyIds = [99, 3, 99, 5],
                DefaultCompanyId = 99,
            },
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action.Result);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(notFound.Value));
        var missingIds = json.RootElement.GetProperty("missingIds").EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();

        Assert.Equal([3, 5, 99], missingIds);
    }

    [Fact]
    public async Task ResolveDictionaryValueAsync_AndValidateInnUniqueness_ReturnNull_ForBlankInput()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, new StubEnrichmentService(), currentUserId: 1);

        var dictionaryResult = await InvokePrivateAsync<string?>(
            controller,
            "ResolveDictionaryValueAsync",
            typeof(CountryDictionaryItem),
            db.Countries,
            "   ",
            "Авто",
            "#2563EB",
            CancellationToken.None);

        var innResult = await InvokePrivateAsync<ActionResult?>(
            controller,
            "ValidateInnUniquenessAsync",
            (Type?)null,
            null,
            null,
            CancellationToken.None);

        Assert.Null(dictionaryResult);
        Assert.Null(innResult);
    }

    [Fact]
    public async Task MigrateLegacyAddressAsync_UsesExistingPrimaryState_WhenAddingAnotherAddress()
    {
        await using var db = await CreateContextAsync();
        var enrichment = new StubEnrichmentService();
        enrichment.AddressResults["г. Самара, ул. Новая, 2"] = CreateCleanedAddress() with
        {
            Result = "г. Самара, ул. Новая, 2",
        };

        var controller = CreateController(db, enrichment, currentUserId: 1);
        var result = new LegalEntityAddressMigrationResultDto
        {
            ValidateWithDadata = true,
        };
        var addresses = new List<AddressRecord>
        {
            new()
            {
                Type = AddressType.Legal,
                Value = "г. Самара, ул. Старая, 1",
                IsPrimary = true,
            },
        };

        await InvokePrivateAsync(
            controller,
            "MigrateLegacyAddressAsync",
            result,
            "legal",
            17,
            "Юрлицо Тест",
            "г. Самара, ул. Новая, 2",
            addresses,
            AddressType.Legal,
            (Action<AddressRecord>)(record => record.LegalEntityId = 17),
            CancellationToken.None);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.DadataMatched);
        Assert.Equal(2, addresses.Count);
        Assert.False(addresses.Last().IsPrimary);
        Assert.Equal(17, addresses.Last().LegalEntityId);
    }

    [Fact]
    public async Task MigrateLegacyAddressAsync_UpdatesExistingAddress_WhenCleanedDataAddsDetails()
    {
        await using var db = await CreateContextAsync();
        var enrichment = new StubEnrichmentService();
        enrichment.AddressResults["г. Самара, ул. Обновление, 3"] = CreateCleanedAddress() with
        {
            Result = "г. Самара, ул. Обновление, 3",
            PostalCode = "446202",
            FiasId = "fias-update",
        };

        var controller = CreateController(db, enrichment, currentUserId: 1);
        var result = new LegalEntityAddressMigrationResultDto
        {
            ValidateWithDadata = true,
        };
        var originalUpdatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var existingAddress = new AddressRecord
        {
            Id = 55,
            Type = AddressType.Legal,
            Value = "г. Самара, ул. Обновление, 3",
            UpdatedAt = originalUpdatedAt,
        };
        var addresses = new List<AddressRecord> { existingAddress };

        await InvokePrivateAsync(
            controller,
            "MigrateLegacyAddressAsync",
            result,
            "legal",
            19,
            "Юрлицо Обновление",
            "г. Самара, ул. Обновление, 3",
            addresses,
            AddressType.Legal,
            (Action<AddressRecord>)(_ => { }),
            CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.DadataMatched);
        Assert.Equal(0, result.Added);
        Assert.Equal("446202", existingAddress.PostalCode);
        Assert.Equal("fias-update", existingAddress.FiasId);
        Assert.True(existingAddress.UpdatedAt > originalUpdatedAt);
        var detail = Assert.Single(result.Details);
        Assert.Equal("Updated", detail.Action);
        Assert.Equal(existingAddress.Id, detail.AddressRecordId);
    }

    private static object? InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(LegalEntitiesController).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var task = await InvokePrivateTaskAsync(target, methodName, genericType: null, args);
        await task;
    }

    private static async Task<T> InvokePrivateAsync<T>(object target, string methodName, Type? genericType, params object?[] args)
    {
        var task = await InvokePrivateTaskAsync(target, methodName, genericType, args);
        await task;
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static Task<T> InvokePrivateAsync<T>(object target, string methodName, params object?[] args)
        => InvokePrivateAsync<T>(target, methodName, genericType: null, args);

    private static Task<Task> InvokePrivateTaskAsync(object target, string methodName, Type? genericType, object?[] args)
    {
        var method = typeof(LegalEntitiesController).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.Name == methodName && candidate.GetParameters().Length == args.Length);

        if (genericType is not null)
        {
            method = method.MakeGenericMethod(genericType);
        }

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(target, args));
        return Task.FromResult(task);
    }

    private static int GetPrivateConstInt(string fieldName)
    {
        var field = typeof(LegalEntitiesController).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (int)field!.GetRawConstantValue()!;
    }

    private static LegalEntitiesController CreateController(
        PaymentContext db,
        StubEnrichmentService enrichmentService,
        int currentUserId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, currentUserId.ToString()),
            new(ClaimTypes.Role, "admin"),
        };

        return new LegalEntitiesController(db, enrichmentService, new NoopFormRequirementsService())
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

    private static CleanedAddress CreateCleanedAddress()
        => new(
            Source: "source",
            Result: "normalized",
            PostalCode: "445000",
            Country: "Россия",
            CountryIsoCode: "RU",
            FederalDistrict: null,
            RegionWithType: "Новая область",
            AreaWithType: "Новый район",
            CityWithType: "Новый город",
            SettlementWithType: "Новый посёлок",
            StreetWithType: "Новая улица",
            House: "10",
            Block: null,
            Flat: "25",
            FiasId: "new-fias",
            FiasLevel: null,
            KladrId: "new-kladr",
            Okato: null,
            Oktmo: null,
            TaxOffice: null,
            TaxOfficeLegal: null,
            GeoLat: "53.1",
            GeoLon: "49.9",
            Qc: 0,
            QcComplete: 0,
            QcHouse: 0,
            QcGeo: 0,
            UnparsedParts: null,
            Timezone: null);

    private sealed class MutableHolder
    {
        public string? Name { get; set; }
    }

    private sealed class ReadOnlyHolder
    {
        public string Name => "Fixed";
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class StubEnrichmentService : ILegalEntityEnrichmentService
    {
        public Dictionary<string, CleanedAddress> AddressResults { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(
                Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult(AddressResults.TryGetValue(rawAddress, out var result) ? result : null);

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
