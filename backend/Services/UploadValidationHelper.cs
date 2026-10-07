using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace PayPlanner.Api.Services;

public sealed record UploadValidationError(string Message);

public static class UploadValidationHelper
{
    // The caller owns the target and cleans up its own partial file on failure.
    // Do not trust declared multipart length or IFormFile.CopyToAsync overrides.
    public static async Task CopyValidatedAsync(IFormFile file, Stream target, long maxSizeBytes, CancellationToken ct)
        => await CopyCoreAsync(file, target, maxSizeBytes, null, ct);

    public static async Task<string> CopyValidatedAndHashAsync(IFormFile file, Stream target, long maxSizeBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyCoreAsync(file, target, maxSizeBytes, hash, ct);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyCoreAsync(IFormFile file, Stream target, long maxSizeBytes, IncrementalHash? hash, CancellationToken ct)
    {
        if (maxSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSizeBytes));
        if (file.Length <= 0 || file.Length > maxSizeBytes)
            throw new InvalidDataException("Некорректный размер файла.");
        await using var input = file.OpenReadStream();
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (read > maxSizeBytes - size || read > file.Length - size)
                throw new InvalidDataException("Файл превышает заявленный или допустимый размер.");
            hash?.AppendData(buffer.AsSpan(0, read));
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            size += read;
        }
        if (size != file.Length) throw new InvalidDataException("Размер полученного файла не совпадает с заявленным.");
    }

    public static UploadValidationError? ValidateRequiredFile(
        IFormFile? file,
        string emptyMessage,
        long? maxSizeBytes = null,
        string? tooLargeMessage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyMessage);

        if (file is null || file.Length <= 0)
        {
            return new UploadValidationError(emptyMessage);
        }

        if (maxSizeBytes is not null && file.Length > maxSizeBytes.Value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tooLargeMessage);
            return new UploadValidationError(tooLargeMessage);
        }

        return null;
    }
}
