using System.Security.Cryptography;

namespace PayPlanner.Api.Services;

/// <summary>Ephemeral authentication namespace for an explicitly isolated restored process.</summary>
public static class RestoredRuntimeAuthentication
{
    public static void Configure(ConfigurationManager configuration)
    {
        if (!configuration.GetValue(RestoredRuntimeIsolation.EnabledKey, false)) return;
        var instanceId = RestoredRuntimeDatabaseIdentity.RequireInstanceId(configuration);
        var scope = $"PayPlanner.Restore.{instanceId:N}";
        // Last configuration provider wins over production environment/user secrets.
        // This key is deliberately never exported: restarting the probe requires a new login.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            ["Jwt:Issuer"] = scope,
            ["Jwt:Audience"] = scope,
        });
    }
}
