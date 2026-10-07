using System.Security.Cryptography;
using System.Text.Json;

namespace PayPlanner.Api.Services.Storage;

// A bounded description of a write planned BEFORE touching the file. This
// format alone grants no write/delete authority: fresh row CAS, held fence and
// descriptor identity/byte verification are also required.
public sealed record StorageStageFileReceipt(int Version, Guid ObjectId, string AdmissionBindingHash,
    long UpperBound, string FileIdentity, long Sequence, long PreviousBytes, string PreviousSha256,
    long PlannedBytes, string PlannedSha256)
{
    public const int MaximumJsonLength = 1024;
    public const int MaximumBlockBytes = 4 * 1024 * 1024;
    public static readonly string EmptySha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

    public static StorageStageFileReceipt Create(Guid objectId, string bindingHash, long upperBound, string fileIdentity)
    {
        var receipt = new StorageStageFileReceipt(1, objectId, bindingHash, upperBound, fileIdentity,
            0, 0, EmptySha256, 0, EmptySha256);
        if (!receipt.IsValid()) throw new ArgumentException("Stage file receipt authority is invalid.");
        return receipt;
    }

    // Call only after the previous planned boundary is physically confirmed.
    // A lost acknowledgement must be resolved by reading the committed receipt;
    // callers cannot increment again merely because an earlier call timed out.
    public StorageStageFileReceipt Plan(long bytes, string sha256)
    {
        if (!IsValid() || Sequence == long.MaxValue || bytes <= PlannedBytes || bytes > UpperBound
            || bytes - PlannedBytes > MaximumBlockBytes || !IsSha256(sha256))
            throw new ArgumentException("Stage write must advance by one bounded block.");
        return this with
        {
            Sequence = Sequence + 1,
            PreviousBytes = PlannedBytes,
            PreviousSha256 = PlannedSha256,
            PlannedBytes = bytes,
            PlannedSha256 = sha256
        };
    }

    public string Serialize()
    {
        if (!IsValid()) throw new InvalidOperationException("Stage file receipt is invalid.");
        var json = JsonSerializer.Serialize(this);
        if (json.Length > MaximumJsonLength) throw new InvalidOperationException("Stage file receipt is too large.");
        return json;
    }

    public static bool TryParse(string? json, out StorageStageFileReceipt? receipt)
    {
        receipt = null;
        if (json is null || json.Length > MaximumJsonLength) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var properties = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
            if (properties.Length != 10 || properties.Distinct(StringComparer.Ordinal).Count() != 10
                || properties.Any(name => name is not ("Version" or "ObjectId" or "AdmissionBindingHash" or "UpperBound"
                    or "FileIdentity" or "Sequence" or "PreviousBytes" or "PreviousSha256" or "PlannedBytes" or "PlannedSha256"))) return false;
            var parsed = JsonSerializer.Deserialize<StorageStageFileReceipt>(json);
            if (parsed is null || !parsed.IsValid()) return false;
            receipt = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private bool IsValid()
    {
        if (Version != 1 || ObjectId == Guid.Empty || !IsSha256(AdmissionBindingHash) || UpperBound <= 0
            || !IsIdentity(FileIdentity) || Sequence < 0 || PreviousBytes < 0 || PlannedBytes < PreviousBytes
            || PlannedBytes > UpperBound || PlannedBytes - PreviousBytes > MaximumBlockBytes
            || !IsSha256(PreviousSha256) || !IsSha256(PlannedSha256)
            || (PreviousBytes == 0 && PreviousSha256 != EmptySha256)) return false;
        return Sequence == 0
            ? PreviousBytes == 0 && PlannedBytes == 0 && PlannedSha256 == EmptySha256
            : PlannedBytes > PreviousBytes && PreviousBytes >= Sequence - 1 && (Sequence != 1 || PreviousBytes == 0);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && IsHex(value);
    private static bool IsHex(string value) => value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsIdentity(string? identity)
    {
        if (identity is null || identity.Length > 128) return false;
        var parts = identity.Split(':');
        return parts.Length == 4 && parts[0] == "windows-stage-file-v1"
            && parts[1].Length == 16 && parts[2].Length == 32 && parts[3].Length == 16 && parts.Skip(1).All(IsHex)
            || parts.Length == 6 && parts[0] == "linux-stage-file-v1"
            && parts[1].Length == 8 && parts[2].Length == 8 && parts[3].Length == 16
            && parts[4].Length == 16 && parts[5].Length == 8 && parts.Skip(1).All(IsHex);
    }
}
