using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassportWire_ModelSerializationOmitsAllServerCryptoColumns(bool web)
    {
        var passport = PassportWireTestSupport.Plain(); foreach (var name in PassportWireTestSupport.CryptoNames) PassportWireTestSupport.Property(name).SetValue(passport, PassportWireTestSupport.FakeCrypto);
        PassportWireTestSupport.AssertSafe(new Client { Name = "Профиль", Passports = [passport] }, web); var json = JsonSerializer.Serialize(passport, PassportWireTestSupport.Options(web)); Assert.Contains("4510", json); Assert.Contains("123456", json);
    }

    public static IEnumerable<object[]> PassportCryptoInputs() => from field in PassportWireTestSupport.CryptoNames from uppercase in new[] { false, true } select new object[] { field, uppercase };
    [Theory]
    [MemberData(nameof(PassportCryptoInputs))]
    public void PassportWire_DeserializerNeverAdmitsServerCryptoColumns(string field, bool uppercase)
    {
        var key = uppercase ? field.ToUpperInvariant() : JsonNamingPolicy.CamelCase.ConvertName(field); var payload = new Dictionary<string, object?> { [key] = PassportWireTestSupport.FakeCrypto, ["series"] = "4510", ["number"] = "123456" };
        var passport = JsonSerializer.Deserialize<ClientPassport>(JsonSerializer.Serialize(payload), PassportWireTestSupport.Options()); Assert.NotNull(passport); var actual = PassportWireTestSupport.Property(field).GetValue(passport);
        Assert.True(actual is null, $"admittedField={field}; uppercase={uppercase}"); Assert.Equal("4510", passport.Series); Assert.Equal("123456", passport.Number);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PassportWire_InputCryptoCannotBecomeStoredValues(bool v2, bool dualWrite)
    {
        await using var db = await PassportWireTestSupport.ContextAsync(dualWrite); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        var passport = JsonSerializer.Deserialize<ClientPassport>(JsonSerializer.Serialize(PassportWireTestSupport.Input()), PassportWireTestSupport.Options())!; var input = new Client { Name = "Новый профиль из JSON", Passports = [passport] };
        var created = Assert.IsType<CreatedResult>(await CreateGraphClientAsync(db, actor, input, v2)); var id = Assert.IsType<Client>(created.Value).Id; var stored = await db.ClientPassports.AsNoTracking().SingleAsync();
        var injected = PassportWireTestSupport.CryptoNames.Where(name => Equals(PassportWireTestSupport.Property(name).GetValue(stored), PassportWireTestSupport.FakeCrypto)).ToArray(); Assert.True(injected.Length == 0, "storedInjectedFields=" + string.Join(',', injected));
        Assert.Equal(id, stored.ClientId); Assert.Equal("4510", stored.Series); Assert.Equal("123456", stored.Number);
        if (dualWrite) { Assert.StartsWith("ppenc:v1:", stored.SeriesEncrypted); Assert.Equal("4510", await PassportWireTestSupport.Encryptor().DecryptAsync(stored.SeriesEncrypted!, $"ClientPassport:Series:{stored.Id}", CancellationToken.None)); Assert.NotNull(stored.SeriesBlindIndex); }
        else Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.Null(PassportWireTestSupport.Property(name).GetValue(stored)));
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassportWire_VersionedCreationResponseKeepsStorageCryptoPrivate(bool v2)
    {
        await using var db = await PassportWireTestSupport.ContextAsync(true); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var input = new Client { Name = "Новый составной профиль", Passports = [PassportWireTestSupport.Plain()] };
        var created = Assert.IsType<CreatedResult>(await CreateGraphClientAsync(db, actor, input, v2)); var reply = Assert.IsType<Client>(created.Value); var stored = await db.ClientPassports.AsNoTracking().SingleAsync(); Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.NotNull(PassportWireTestSupport.Property(name).GetValue(stored)));
        Assert.Equal("4510", await PassportWireTestSupport.Encryptor().DecryptAsync(stored.SeriesEncrypted!, $"ClientPassport:Series:{stored.Id}", CancellationToken.None)); PassportWireTestSupport.AssertSafe(reply); Assert.Equal("4510", Assert.Single(reply.Passports).Series); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassportWire_CryptoColumnsRemainMappedAndRoundTripThroughEf(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportWireTestSupport.ContextAsync(false);
        var (actor, _) = await SeedBulkCasesAsync(db); var owner = await db.Clients.Select(row => row.Id).SingleAsync(); var passport = PassportWireTestSupport.Plain(); passport.ClientId = owner; db.ClientPassports.Add(passport); await db.SaveChangesAsync();
        foreach (var name in PassportWireTestSupport.CryptoNames) PassportWireTestSupport.Property(name).SetValue(passport, "stored-test-" + name); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); var stored = await db.ClientPassports.AsNoTracking().SingleAsync(); Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.Equal("stored-test-" + name, PassportWireTestSupport.Property(name).GetValue(stored)));
        var type = db.Model.FindEntityType(typeof(ClientPassport))!; var table = StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema()); var shape = string.Join('|', type.GetProperties().OrderBy(property => property.Name).Select(property => $"{property.Name}:{property.GetColumnName(table)}:{property.GetColumnType()}:{property.IsNullable}")); Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.NotNull(type.FindProperty(name)));
        Console.WriteLine($"PassportEFShape postgres={postgres} sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))}");
    }
}
