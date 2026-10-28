using System.Text.RegularExpressions;
using System.Text.Json;

namespace PayPlanner.TestShards;

public sealed record ExecutionProfile(int SchemaVersion, long FastThresholdTicks, int BatchCount,
    string[] ExclusiveClasses, string[] ExclusiveMethods, string[] ReviewedMethods)
{
    public static ExecutionProfile Bounded() => new(1, TimeSpan.TicksPerSecond, 4,
        Names("DatabaseBackupPostgreSqlCancellationTests", "DatabaseBackupSnapshotSessionTests",
            "DatabaseRestoreDrillEndToEndPostgreSqlTests", "DatabaseRestoreManagedReplicasTests",
            "TaskFileQueueWireIntegrationTests", "VerifiedStorageReadLoadTests"),
        Names(
            "CaseDocumentRetirementBrowserTests.RealBrowserRetiresOwnedAttemptAndSeparatelyUploadsDocumentAndVersion",
            "DatabaseBackupServiceS3Tests.RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing",
            "InternalChatManagedWireTests.RealBrowserChatYandexCopiesKeepChangedDraftAcrossReloadAndExplicitResume",
            "ManagedCaseDocumentWireTests.RealBrowserCourtYandexCopiesKeepControlsAndShowPendingThenReadySummary",
            "ManagedCaseDocumentWireTests.RealBrowserFinancialLostReadyAcknowledgementDiscoversOriginalWinnerWithoutNewPost",
            "ManagedCaseDocumentWireTests.RealBrowserFinancialYandexCopiesPreserveAttemptAndUnsavedCaseFields",
            "ManagedCaseDocumentWireTests.RealBrowserGeneratedLostAckReplaysOriginalAttemptWithoutDuplicate",
            "MigrationsAssemblyStartupTests.ApiStartsAndMigratesWithItsOwnDependencyManifest",
            "StorageStageCapacityServiceTests.IndependentCheckpointWriterRetainsExactBoundaryAcrossKill",
            "StorageStageCapacityServiceTests.IndependentEvidenceZipMeasuresSuccessfulProducerResources",
            "StorageStageCapacityServiceTests.IndependentEvidenceZipPartialFaultRetainsBytesAndDebtAcrossKill",
            "StorageStageCapacityServiceTests.IndependentOriginalReadMeasuresSuccessfulProducerResources",
            "StorageStageCapacityServiceTests.IndependentOriginalReadPartialFaultRetainsBytesAndDebtAcrossKill",
            "StorageStageCapacityServiceTests.IndependentOriginalReadProducerRetainsExactBoundaryAcrossKill",
            "StorageStageCapacityServiceTests.IndependentPartialCheckpointFaultRetainsBytesAndDebtAcrossKill",
            "StorageStageCapacityServiceTests.IndependentSignaturePdfMeasuresSuccessfulProducerResources",
            "StorageStageCapacityServiceTests.IndependentSignaturePdfPartialFaultRetainsBytesAndDebtAcrossKill",
            "StorageStageCapacityServiceTests.IndependentWriterFenceSurvivesAgeAndAllowsOnlyProvenRecovery",
            "StorageStageFileIdentityTests.ActualIdentityIsStableAcrossIndependentProcessAndDoesNotChangeBytesOrPosition",
            "StorageStageRecoveryWireTests.BrowserConfirmsOnlyCompleteStageAgainstOwnedBackend",
            "StorageStageReleasedMarkerWireTests.BrowserRemovesOnlyProvenReleasedMarkerAndKeepsForeignBytes",
            "StorageStageWriterRecoveryWireTests.BrowserRecoveryRechecksChangedFileAndRetainsLiveWriter",
            "SyntheticWritableBackendProcessTests.BrowserWorkingUploadRecoversLostResponseWithoutDuplicateInEitherCard"),
        TestTimings.ReadBaseline().Methods.Select(row => row.FullyQualifiedName).Order(StringComparer.Ordinal).ToArray());

    private static string[] Names(params string[] names) => names.Select(name => "PayPlanner.Api.Tests." + name)
        .Order(StringComparer.Ordinal).ToArray();
}

public sealed record ExecutionBatch(string Id, string Lane, PlannedCase[] Cases, decimal EstimatedTicks);
public sealed record BatchRun(int SchemaVersion, string Revision, string ManifestSha256, int Shard, string Batch,
    string Lane, string AssemblySha256, DateTime StartedUtc, DateTime FinishedUtc, int ExitCode, string? ErrorKind);
public sealed record SubsetQualification(bool Qualified, bool SubsetPassed, string Suite, int Shard, int Cases,
    string ManifestSha256, string Revision);

/// <summary>Scheduling changes execution order only; every discovered case remains in its outer shard.</summary>
public static class ExecutionScheduling
{
    // This theory uses independent databases per row. Its four 50-case groups
    // were checked against the exact catalog and exercised with two workers.
    public const string SplitBankScopeTheory = "PayPlanner.Api.Tests.MoneyOperationsControllerTests.BankPackageScope_RejectsWholeForbiddenPackageBeforeReadOrMutation";
    private const ulong Gib = 1024UL * 1024 * 1024;
    private const ulong OrdinaryHeapBytes = 2 * Gib;
    private const ulong ExclusiveHeapBytes = 3 * Gib;
    private const ulong ReservedBytes = 4 * Gib;

    public static void RequireSerialXunit(string runtime)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime, "xunit.runner.json")));
        var root = document.RootElement;
        ShardPlan.Require(root.TryGetProperty("parallelizeTestCollections", out var parallel) && parallel.ValueKind == JsonValueKind.False
            && root.TryGetProperty("maxParallelThreads", out var threads) && threads.TryGetInt32(out var count) && count == 1,
            "Bounded process execution requires serial xUnit collections and one thread inside every worker.");
    }

    public static void Validate(ExecutionProfile profile)
    {
        ShardPlan.Require(profile.SchemaVersion == 1 && profile.FastThresholdTicks == TimeSpan.TicksPerSecond
            && profile.BatchCount == 4, "Unsupported bounded execution profile.");
        foreach (var names in new[] { profile.ExclusiveClasses, profile.ExclusiveMethods, profile.ReviewedMethods })
            ShardPlan.Require(names is not null && names.SequenceEqual(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                && names.All(name => Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_+`]*(?:\.[A-Za-z_][A-Za-z0-9_+`]*)+\z", RegexOptions.CultureInvariant)),
                "Execution exclusions must be canonical exact names.");
    }

    public static string Lane(TestPlan plan, string method)
    {
        var profile = plan.Execution ?? throw new InvalidDataException("A bounded execution profile is required.");
        var className = method[..method.LastIndexOf('.')];
        if (!profile.ReviewedMethods.Contains(method, StringComparer.Ordinal)
            || profile.ExclusiveClasses.Contains(className, StringComparer.Ordinal)
            || profile.ExclusiveMethods.Contains(method, StringComparer.Ordinal)) return "exclusive";
        var timing = plan.Timings?.FirstOrDefault(row => row.FullyQualifiedName == method);
        return timing is not null && timing.AverageTicks < profile.FastThresholdTicks ? "fast" : "heavy";
    }

    public static ExecutionBatch[] Batches(TestPlan plan, int shard, string suite = "all", int? batchCount = null)
    {
        ShardPlan.Require(shard >= 0 && shard < plan.ShardCount, "Invalid shard index.");
        ShardPlan.Require(suite is "all" or "fast" or "heavy", "Unsupported execution suite.");
        var profile = plan.Execution ?? throw new InvalidDataException("A bounded execution profile is required.");
        Validate(profile);
        var partitions = batchCount ?? profile.BatchCount;
        ShardPlan.Require(partitions is 1 or 2 or 4 && partitions <= profile.BatchCount, "Unsupported execution partition count.");
        var estimates = plan.Timings?.ToDictionary(row => row.FullyQualifiedName, row => row.AverageTicks, StringComparer.Ordinal)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var fallback = estimates.Count == 0 ? TimeSpan.TicksPerSecond : estimates.Values.Order().ElementAt(estimates.Count / 2);
        var classes = profile.ExclusiveClasses.ToHashSet(StringComparer.Ordinal);
        var methods = profile.ExclusiveMethods.ToHashSet(StringComparer.Ordinal);
        var reviewed = profile.ReviewedMethods.ToHashSet(StringComparer.Ordinal);
        string GroupLane(string method) => !reviewed.Contains(method) || classes.Contains(method[..method.LastIndexOf('.')]) || methods.Contains(method) ? "exclusive"
            : estimates.TryGetValue(method, out var duration) && duration < profile.FastThresholdTicks ? "fast" : "heavy";
        var groups = plan.Cases.Where(row => row.Shard == shard).GroupBy(row => row.FullyQualifiedName, StringComparer.Ordinal)
            .SelectMany(group => group.Key == SplitBankScopeTheory && GroupLane(group.Key) != "exclusive"
                ? group.GroupBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(part => (Method: group.Key, Identity: part.Key, Cases: part.ToArray()))
                : new[] { (Method: group.Key, Identity: "", Cases: group.ToArray()) }).ToArray();
        var result = new List<ExecutionBatch>();
        foreach (var lane in new[] { "fast", "heavy", "exclusive" })
        {
            if (suite == "fast" && lane != "fast" || suite == "heavy" && lane == "fast") continue;
            decimal Weight((string Method, string Identity, PlannedCase[] Cases) group) => (decimal)estimates.GetValueOrDefault(group.Method, fallback) * group.Cases.Length;
            var selected = groups.Where(group => GroupLane(group.Method) == lane).OrderByDescending(Weight)
                .ThenBy(group => group.Method, StringComparer.Ordinal).ThenBy(group => group.Identity, StringComparer.Ordinal).ToArray();
            var count = Math.Min(selected.Length, lane == "exclusive" ? 1 : partitions);
            var loads = new decimal[count];
            var rows = Enumerable.Range(0, count).Select(_ => new List<PlannedCase>()).ToArray();
            foreach (var group in selected)
            {
                var index = Enumerable.Range(0, count).OrderBy(index => loads[index]).ThenBy(index => index).First();
                rows[index].AddRange(group.Cases);
                loads[index] += Weight(group);
            }
            for (var index = 0; index < count; index++) result.Add(new($"{lane}-{index}", lane,
                rows[index].OrderBy(row => row.FullyQualifiedName, StringComparer.Ordinal).ThenBy(row => row.Id).ToArray(), loads[index]));
        }
        return result.ToArray();
    }

    public static int WorkerBudget(string requested, ulong availableBytes, int processors)
    {
        ShardPlan.Require(requested is "auto" or "1" or "2" or "4", "Workers must be auto, 1, 2 or 4.");
        ShardPlan.Require(processors > 0, "The host processor count is unavailable.");
        ShardPlan.Require(availableBytes >= Gib, "Available RAM fell below the 1 GiB execution floor; retained results are incomplete.");
        var safe = availableBytes >= ReservedBytes + 4 * OrdinaryHeapBytes && processors >= 8 ? 4
            : availableBytes >= ReservedBytes + 2 * OrdinaryHeapBytes && processors >= 4 ? 2 : 1;
        return requested == "auto" ? safe : Math.Min(safe, int.Parse(requested, System.Globalization.CultureInfo.InvariantCulture));
    }

    // Partitioning does not launch processes. Keep enough independent batches for a
    // later memory increase while retaining the requested and processor limits.
    public static int PartitionCapacity(string requested, int processors)
    {
        ShardPlan.Require(requested is "auto" or "1" or "2" or "4", "Workers must be auto, 1, 2 or 4.");
        ShardPlan.Require(processors > 0, "The host processor count is unavailable.");
        var processorLimit = processors >= 8 ? 4 : processors >= 4 ? 2 : 1;
        return requested == "auto" ? processorLimit : Math.Min(processorLimit, int.Parse(requested, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool CanLaunchAdditional(int active, int initialBudget, ulong availableBytes, string lane = "heavy")
    {
        ShardPlan.Require(active >= 0 && initialBudget is 1 or 2 or 4, "Invalid execution worker state.");
        ShardPlan.Require(lane is "fast" or "heavy" or "exclusive", "Unsupported execution lane.");
        // The first process needs the same reserve as its siblings. The 1 GiB
        // floor is an emergency stop, not enough headroom to admit a test host.
        var heap = lane == "exclusive" ? ExclusiveHeapBytes : OrdinaryHeapBytes;
        return active < initialBudget && (lane != "exclusive" || active == 0)
            && availableBytes >= ReservedBytes + heap;
    }

    public static void ConfigureWorkerEnvironment(IDictionary<string, string?> environment, string lane)
    {
        ShardPlan.Require(lane is "fast" or "heavy" or "exclusive", "Unsupported execution lane.");
        environment["Email__Enabled"] = "false";
        environment["BackgroundServices__Enabled"] = "false";
        environment["DOTNET_PROCESSOR_COUNT"] = "2";
        foreach (var key in environment.Keys.Where(key => key.StartsWith("DOTNET_GCHeapHardLimit", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("COMPlus_GCHeapHardLimit", StringComparison.OrdinalIgnoreCase)).ToArray()) environment.Remove(key);
        environment["DOTNET_GCHeapHardLimit"] = (lane == "exclusive" ? ExclusiveHeapBytes : OrdinaryHeapBytes)
            .ToString("X", System.Globalization.CultureInfo.InvariantCulture);
    }
}
