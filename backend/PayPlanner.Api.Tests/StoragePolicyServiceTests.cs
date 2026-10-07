using Microsoft.Extensions.Options;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StoragePolicyServiceTests
{
    [Fact]
    public void EvaluatePreservesConfiguredPriorityAfterNormalizationAndCapabilityFiltering()
    {
        var options = new StorageOrchestrationOptions
        {
            DefaultPolicy = new()
            {
                WriteProviders = [" mirror ", "local", "MIRROR", "limited", "missing"],
                ReadProviders = ["missing", "z-recovery", "mirror", "local", "limited", "Z-RECOVERY"],
                RequireConditionalCreate = true,
                RequireRangeReads = true
            }
        };
        var result = CreateService(options, new FakeProvider("mirror", true, true), new FakeProvider("z-recovery", true, true),
            new FakeProvider("limited", false, false)).Evaluate("document");
        Assert.Equal(["mirror", "local"], result.WriteProviders);
        Assert.Equal(["z-recovery", "mirror", "local"], result.ReadProviders);
        Assert.Equal(["limited", "local", "mirror", "missing", "z-recovery"], result.Providers.Select(p => p.ProviderKey));
    }

    [Fact]
    public void Evaluate_DefaultsToValidLocalOnlyDryRunWithoutEnablingRouting()
    {
        var result = CreateService(new StorageOrchestrationOptions { PolicyDryRunEnabled = true }).Evaluate("document");

        Assert.True(result.IsValid);
        Assert.True(result.EvaluationEnabled);
        Assert.False(result.RoutingEnabled);
        Assert.Equal("local-v1", result.PolicyVersion);
        Assert.Equal(["local"], result.WriteProviders);
        Assert.Equal(["local"], result.ReadProviders);
    }

    [Fact]
    public void Evaluate_RejectsMissingProvidersAndImpossibleCopyCount()
    {
        var options = new StorageOrchestrationOptions
        {
            PolicyDryRunEnabled = true,
            Policies = new Dictionary<string, StoragePolicyDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["evidence"] = new()
                {
                    Version = "evidence-v2",
                    RequiredCopies = 2,
                    WriteProviders = ["local", "missing"],
                    ReadProviders = ["missing"],
                },
            },
        };

        var result = CreateService(options).Evaluate("evidence");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, item => item.Contains("not registered", StringComparison.Ordinal));
        Assert.Contains(result.Errors, item => item.Contains("requires 2 copies", StringComparison.Ordinal));
        Assert.Contains(result.Errors, item => item.Contains("no eligible read", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_FiltersProvidersThatDoNotMeetCapabilities()
    {
        var options = new StorageOrchestrationOptions
        {
            Policies = new Dictionary<string, StoragePolicyDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["video"] = new()
                {
                    Version = "video-v1",
                    WriteProviders = ["limited"],
                    ReadProviders = ["limited"],
                    RequireConditionalCreate = true,
                    RequireRangeReads = true,
                },
            },
        };

        var result = CreateService(options, new FakeProvider("limited", conditional: false, ranges: false)).Evaluate("video");

        Assert.False(result.IsValid);
        Assert.Empty(result.WriteProviders);
        Assert.Empty(result.ReadProviders);
        Assert.Equal(2, Assert.Single(result.Providers).Reasons.Count);
    }

    private static StoragePolicyService CreateService(StorageOrchestrationOptions options, params IStorageProvider[] additional)
    {
        var all = new IStorageProvider[] { new FakeProvider("local", conditional: true, ranges: true) }
            .Concat(additional)
            .ToArray();
        return new StoragePolicyService(new StaticOptionsMonitor<StorageOrchestrationOptions>(options), new StorageProviderRegistry(all));
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeProvider(string key, bool conditional, bool ranges) : IStorageProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities { get; } = new(true, ranges, conditional, false, false, false, false, false, false);
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
