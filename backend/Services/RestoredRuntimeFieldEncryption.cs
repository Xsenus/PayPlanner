using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PayPlanner.Api.Services;

/// <summary>Uses the caller's read-only restored database snapshot; no writes or key export.</summary>
public static class RestoredRuntimeFieldEncryption
{
    private const string Failure = "Не подтверждено чтение зашифрованных паспортных данных восстановленной CRM. Проверьте отдельно восстановленные ключи.";
    private static readonly string[] Fields = ["Series", "Number", "IssuedBy", "IssuedAt", "DepartmentCode", "BirthPlacePassport", "BirthPlaceFias"];

    public static async Task<long> VerifyConnectionAsync(DbConnection connection, IServiceProvider services, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 120;
            var encrypted = Fields.Select(field => $"\"{field}Encrypted\"").ToArray();
            command.CommandText = $"SELECT \"Id\", {string.Join(", ", encrypted)}, \"SeriesBlindIndex\", \"NumberBlindIndex\" FROM \"ClientPassports\" WHERE {string.Join(" OR ", encrypted.Select(column => column + " IS NOT NULL"))} OR \"SeriesBlindIndex\" IS NOT NULL OR \"NumberBlindIndex\" IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            long count = 0;
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetInt32(0);
                if (id <= 0) throw new InvalidOperationException();
                var encryptor = services.GetRequiredService<IFieldEncryptor>();
                // Only indexed plaintext is retained until its index columns have been read.
                string?[] ciphertexts = new string?[2];
                string?[] indexedValues = new string?[2];
                for (var index = 0; index < Fields.Length; index++)
                {
                    if (reader.IsDBNull(index + 1)) continue;
                    var ciphertext = reader.GetString(index + 1);
                    if (ciphertext.Length > 65536) throw new InvalidOperationException();
                    var plaintext = await encryptor.DecryptAsync(ciphertext,
                        $"ClientPassport:{Fields[index]}:{id.ToString(CultureInfo.InvariantCulture)}", token);
                    if (index < 2) { ciphertexts[index] = ciphertext; indexedValues[index] = plaintext; }
                    count++;
                }
                for (var index = 0; index < 2; index++)
                {
                    var storedIndex = reader.IsDBNull(8 + index) ? null : reader.GetString(8 + index);
                    if (ciphertexts[index] is null)
                    {
                        if (storedIndex is not null) throw new InvalidOperationException();
                        continue;
                    }
                    var expected = await encryptor.ComputeBlindIndexForCiphertextAsync(ciphertexts[index]!, indexedValues[index]!, $"ClientPassport:{Fields[index]}", token);
                    if (storedIndex is null || storedIndex.Length != 64 || expected.Length != 64
                        || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedIndex), Encoding.ASCII.GetBytes(expected)))
                        throw new InvalidOperationException();
                }
            }
            return count;
        }
        catch (Exception error) when (error is InvalidOperationException or CryptographicException or DbException or ArgumentException
            or FormatException or InvalidCastException or IOException or UnauthorizedAccessException or KeyNotFoundException or TimeoutException or NotSupportedException)
        {
            // Never carry plaintext, ciphertext, key IDs, provider responses, or inner exceptions into startup logs.
            throw new InvalidOperationException(Failure);
        }
    }
}
