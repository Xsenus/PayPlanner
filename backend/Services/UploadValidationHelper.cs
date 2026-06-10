using Microsoft.AspNetCore.Http;

namespace PayPlanner.Api.Services;

public sealed record UploadValidationError(string Message);

public static class UploadValidationHelper
{
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
