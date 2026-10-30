using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Fingerprints the complete persisted edit baseline, not permissions or a draft.
/// Callers must load all owned addresses and all linked client IDs before computing it.
/// </summary>
public static class CompanyProfileEditVersion
{
    private static readonly PropertyInfo[] CompanyProperties = ScalarProperties(typeof(LegalEntity));
    private static readonly PropertyInfo[] AddressProperties = ScalarProperties(typeof(AddressRecord));

    public static bool IsValid(string? value) => CompanyDocumentImagePrintDefaultsService.IsValidVersion(value);

    public static string Compute(LegalEntity company, IEnumerable<AddressRecord> addresses, IEnumerable<int> clientIds, string imageDefaultsVersion)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(clientIds);
        if (!IsValid(imageDefaultsVersion)) throw new ArgumentException("An image-defaults version is required.", nameof(imageDefaultsVersion));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "company-profile-edit-v1");
        AppendObject(hash, company, CompanyProperties);
        AppendText(hash, "addresses");
        var orderedAddresses = addresses.OrderBy(row => row.Id).ToArray();
        AppendNumber(hash, orderedAddresses.Length);
        foreach (var address in orderedAddresses) AppendObject(hash, address, AddressProperties);
        AppendText(hash, "clients");
        var orderedClients = clientIds.Distinct().Order().ToArray();
        AppendNumber(hash, orderedClients.Length);
        foreach (var id in orderedClients) AppendNumber(hash, id);
        AppendText(hash, "image-defaults");
        AppendText(hash, imageDefaultsVersion.ToUpperInvariant());
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PropertyInfo[] ScalarProperties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0
            && (property.PropertyType == typeof(string) || property.PropertyType.IsValueType))
        .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();

    private static void AppendObject(IncrementalHash hash, object value, IEnumerable<PropertyInfo> properties)
    {
        foreach (var property in properties)
        {
            AppendText(hash, property.Name);
            var scalar = property.GetValue(value);
            AppendText(hash, scalar switch
            {
                null => null,
                string text => text,
                bool flag => flag ? "1" : "0",
                // PostgreSQL stores microseconds; SQLite may preserve a seventh decimal.
                // Kind is not part of the persisted UTC wall-clock value.
                DateTime date => (date.Ticks / 10).ToString(CultureInfo.InvariantCulture),
                DateTimeOffset date => (date.UtcTicks / 10).ToString(CultureInfo.InvariantCulture),
                IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported profile field {property.Name}."),
            });
        }
    }

    private static void AppendNumber(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendText(IncrementalHash hash, string? value)
    {
        AppendNumber(hash, value?.Length ?? -1);
        if (value is null) return;
        // Canonical UTF-16LE, including embedded nulls/surrogates. Do not allocate a
        // second byte buffer for legacy image strings that may be several MiB.
        if (BitConverter.IsLittleEndian)
        {
            hash.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
            return;
        }
        Span<byte> buffer = stackalloc byte[2048];
        for (var offset = 0; offset < value.Length;)
        {
            var count = Math.Min(buffer.Length / 2, value.Length - offset);
            for (var index = 0; index < count; index++) BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(index * 2, 2), value[offset + index]);
            hash.AppendData(buffer[..(count * 2)]);
            offset += count;
        }
    }
}
