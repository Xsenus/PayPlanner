using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

internal static class PassportWireTestSupport
{
    internal const string FakeCrypto = "client-supplied-opaque";
    internal static readonly string[] CryptoNames =
    [
        nameof(ClientPassport.SeriesEncrypted), nameof(ClientPassport.SeriesBlindIndex), nameof(ClientPassport.NumberEncrypted), nameof(ClientPassport.NumberBlindIndex),
        nameof(ClientPassport.IssuedByEncrypted), nameof(ClientPassport.IssuedAtEncrypted), nameof(ClientPassport.DepartmentCodeEncrypted), nameof(ClientPassport.BirthPlacePassportEncrypted), nameof(ClientPassport.BirthPlaceFiasEncrypted),
    ];
    internal static JsonSerializerOptions Options(bool web = true) => new(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    internal static AeadFieldEncryptor Encryptor() => new(new LocalKeys());
    internal static async Task<PaymentContext> ContextAsync(bool dualWrite)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldEncryption:Enabled"] = dualWrite.ToString(),
            ["FieldEncryption:ClientPassportDualWriteEnabled"] = dualWrite.ToString(),
        }).Build();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options,
            clientPassportEncryption: new ClientPassportEncryptionDualWriteService(Encryptor(), configuration));
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }
    internal static ClientPassport Plain() => new()
    {
        Series = "4510",
        Number = "123456",
        IssuedBy = "Синтетический отдел",
        IssuedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        DepartmentCode = "540-001",
        BirthPlacePassport = "Тестовый город",
        BirthPlaceFias = "Тестовый ФИАС",
        IsPrimary = true,
    };
    internal static PropertyInfo Property(string name) => typeof(ClientPassport).GetProperty(name)!;
    internal static Dictionary<string, object?> Input()
    {
        var result = new Dictionary<string, object?> { ["series"] = "4510", ["number"] = "123456", ["issuedBy"] = "Синтетический отдел", ["issuedAt"] = "2020-01-02T00:00:00Z", ["departmentCode"] = "540-001", ["birthPlacePassport"] = "Тестовый город", ["birthPlaceFias"] = "Тестовый ФИАС", ["isPrimary"] = true };
        foreach (var name in CryptoNames) result[JsonNamingPolicy.CamelCase.ConvertName(name)] = FakeCrypto;
        return result;
    }
    internal static void AssertSafe(object? value, bool web = true)
    {
        var json = JsonSerializer.Serialize(value, Options(web)); var leaked = CryptoNames.Where(name => json.Contains('"' + (web ? JsonNamingPolicy.CamelCase.ConvertName(name) : name) + '"', StringComparison.Ordinal)).ToArray();
        Assert.True(leaked.Length == 0, "leakedCryptoFields=" + string.Join(',', leaked)); Assert.DoesNotContain("ppenc:v1:", json, StringComparison.Ordinal); Assert.DoesNotContain(FakeCrypto, json, StringComparison.Ordinal);
    }
    private sealed class LocalKeys : IFieldEncryptionKeyStore
    {
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync("passport-wire-local", ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (keyId != "passport-wire-local") throw new InvalidOperationException("Unknown local test key");
            return ValueTask.FromResult(new FieldEncryptionKeyMaterial(keyId, Enumerable.Repeat((byte)73, 32).ToArray(), Enumerable.Repeat((byte)29, 32).ToArray()));
        }
    }
}
