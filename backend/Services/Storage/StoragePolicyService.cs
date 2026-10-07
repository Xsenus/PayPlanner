using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.Storage;

public sealed class StorageOrchestrationOptions
{
    public const string SectionName = "StorageOrchestration";
    public bool Enabled { get; set; }
    public bool PolicyDryRunEnabled { get; set; }
    public string LocalRoot { get; set; } = string.Empty;
    public bool WorkerEnabled { get; set; }
    public bool AutoTaskFilePublicationEnabled { get; set; }
    public bool AutoCaseDocumentPublicationEnabled { get; set; }
    public bool MigrationCapacityChecksEnabled { get; set; }
    public long MigrationMinimumFreeBytes { get; set; } = 64 * 1024 * 1024;
    public bool RepairCapacityChecksEnabled { get; set; }
    public long RepairMinimumFreeBytes { get; set; } = 64 * 1024 * 1024;
    public bool UploadCapacityChecksEnabled { get; set; }
    public long UploadMinimumFreeBytes { get; set; } = 64 * 1024 * 1024;
    public List<string> ProductReadEnabledClasses { get; set; } = [];
    public List<string> ProductWriteEnabledClasses { get; set; } = [];
    public int WorkerBatchSize { get; set; } = 20;
    public int WorkerPollSeconds { get; set; } = 10;
    public int WorkerLeaseSeconds { get; set; } = 120;
    public int WorkerMaxAttempts { get; set; } = 8;
    public int ProviderFailureThreshold { get; set; } = 3;
    public int ProviderCircuitBreakSeconds { get; set; } = 60;
    public Dictionary<string, string> ProviderConnections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public StoragePolicyDefinition DefaultPolicy { get; set; } = StoragePolicyDefinition.LocalOnly();
    public Dictionary<string, StoragePolicyDefinition> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StoragePolicyDefinition
{
    public string Version { get; set; } = "local-v1";
    public int RequiredCopies { get; set; } = 1;
    public List<string> WriteProviders { get; set; } = ["local"];
    public List<string> ReadProviders { get; set; } = ["local"];
    public bool RequireConditionalCreate { get; set; }
    public bool RequireRangeReads { get; set; }
    public int? RetentionDays { get; set; }
    public bool LegalHold { get; set; }

    public static StoragePolicyDefinition LocalOnly() => new();
}

public sealed record StoragePolicyProviderDecision(
    string ProviderKey,
    bool Registered,
    bool EligibleForWrite,
    bool EligibleForRead,
    IReadOnlyList<string> Reasons);

public sealed record StoragePolicyEvaluation(
    string DataClass,
    string PolicyVersion,
    bool EvaluationEnabled,
    bool RoutingEnabled,
    bool IsValid,
    int RequiredCopies,
    IReadOnlyList<string> WriteProviders,
    IReadOnlyList<string> ReadProviders,
    int? RetentionDays,
    bool LegalHold,
    IReadOnlyList<string> Errors,
    IReadOnlyList<StoragePolicyProviderDecision> Providers);

public interface IStoragePolicyService
{
    StoragePolicyEvaluation Evaluate(string dataClass);
}

public sealed class StoragePolicyService(
    IOptionsMonitor<StorageOrchestrationOptions> options,
    IStorageProviderRegistry providers) : IStoragePolicyService
{
    public StoragePolicyEvaluation Evaluate(string dataClass)
    {
        var normalizedDataClass = string.IsNullOrWhiteSpace(dataClass) ? "default" : dataClass.Trim();
        var settings = options.CurrentValue;
        var configured = settings.Policies.TryGetValue(normalizedDataClass, out var selected)
            ? selected!
            : settings.DefaultPolicy;
        var policy = CloneAndNormalize(configured);
        var errors = new List<string>();

        if (policy.RequiredCopies < 1) errors.Add("RequiredCopies must be at least 1.");
        if (policy.RetentionDays is < 0) errors.Add("RetentionDays must not be negative.");

        var providerKeys = policy.WriteProviders.Concat(policy.ReadProviders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var decisions = new List<StoragePolicyProviderDecision>(providerKeys.Length);
        foreach (var providerKey in providerKeys)
        {
            var reasons = new List<string>();
            var registered = providers.TryGet(providerKey, out var provider);
            var writeRequested = policy.WriteProviders.Contains(providerKey, StringComparer.OrdinalIgnoreCase);
            var readRequested = policy.ReadProviders.Contains(providerKey, StringComparer.OrdinalIgnoreCase);
            var writeEligible = registered && writeRequested;
            var readEligible = registered && readRequested;

            if (!registered)
            {
                reasons.Add("Provider is not registered.");
                errors.Add($"Provider '{providerKey}' is not registered.");
            }
            else
            {
                if (policy.RequireConditionalCreate && !provider!.Capabilities.SupportsConditionalCreate)
                {
                    writeEligible = false;
                    reasons.Add("Conditional create is required but unsupported.");
                }
                if (policy.RequireRangeReads && !provider!.Capabilities.SupportsRangeReads)
                {
                    readEligible = false;
                    reasons.Add("Range reads are required but unsupported.");
                }
            }

            decisions.Add(new StoragePolicyProviderDecision(providerKey, registered, writeEligible, readEligible, reasons));
        }

        var byKey = decisions.ToDictionary(item => item.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var eligibleWriters = policy.WriteProviders.Where(key => byKey[key].EligibleForWrite).Select(key => byKey[key].ProviderKey).ToArray();
        var eligibleReaders = policy.ReadProviders.Where(key => byKey[key].EligibleForRead).Select(key => byKey[key].ProviderKey).ToArray();
        if (eligibleWriters.Length < policy.RequiredCopies)
        {
            errors.Add($"Policy requires {policy.RequiredCopies} copies but only {eligibleWriters.Length} eligible write providers are available.");
        }
        if (eligibleReaders.Length == 0) errors.Add("Policy has no eligible read provider.");

        return new StoragePolicyEvaluation(
            normalizedDataClass,
            string.IsNullOrWhiteSpace(policy.Version) ? "unversioned" : policy.Version,
            settings.PolicyDryRunEnabled,
            settings.Enabled,
            errors.Count == 0,
            policy.RequiredCopies,
            eligibleWriters,
            eligibleReaders,
            policy.RetentionDays,
            policy.LegalHold,
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            decisions);
    }

    private static StoragePolicyDefinition CloneAndNormalize(StoragePolicyDefinition source)
        => new()
        {
            Version = source.Version?.Trim() ?? string.Empty,
            RequiredCopies = source.RequiredCopies,
            WriteProviders = NormalizeProviders(source.WriteProviders),
            ReadProviders = NormalizeProviders(source.ReadProviders),
            RequireConditionalCreate = source.RequireConditionalCreate,
            RequireRangeReads = source.RequireRangeReads,
            RetentionDays = source.RetentionDays,
            LegalHold = source.LegalHold,
        };

    private static List<string> NormalizeProviders(IEnumerable<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
