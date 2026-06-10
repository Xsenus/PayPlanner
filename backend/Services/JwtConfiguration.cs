using Microsoft.Extensions.Configuration;

namespace PayPlanner.Api.Services;

public static class JwtConfiguration
{
    public const int MinSecretLength = 32;

    public static string GetRequiredSecret(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Jwt:Secret must be configured through user-secrets, environment variables, or a secret store.");
        }

        if (secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException($"Jwt:Secret must be at least {MinSecretLength} characters long.");
        }

        return secret;
    }
}
