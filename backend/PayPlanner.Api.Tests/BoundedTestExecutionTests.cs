using System.Text.Json;
using System.Xml.Linq;
using PayPlanner.TestShards;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BoundedTestExecutionTests
{
    [Fact]
    public async Task FailedOutputSinkStopsOnlyItsOwnedProcessAndDrainsBothPipes()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        var drain = OwnedOutputDrain.WaitAsync(exited.Task, stdout.Task, stderr.Task, () =>
        {
            stops++;
            exited.SetResult();
            stderr.SetResult();
        });
        stdout.SetException(new IOException("The owned log sink is full."));
        await Assert.ThrowsAsync<IOException>(() => drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, stops);
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.True(stderr.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NormalOutputCompletionWaitsForTheChildWithoutTerminatingIt()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = OwnedOutputDrain.WaitAsync(exited.Task, Task.CompletedTask, Task.CompletedTask,
            () => throw new InvalidOperationException("A healthy owned child must not be stopped."));
        Assert.False(drain.IsCompleted);
        exited.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LanesKeepTheExactDisjointCatalogAndNeverSplitTheoryRows()
    {
        using var fixture = new Fixture();
        var all = Enumerable.Range(0, 4).SelectMany(shard => ExecutionScheduling.Batches(fixture.Plan, shard)).ToArray();
        Assert.Equal(fixture.Plan.Cases.Select(row => row.Id).Order(), all.SelectMany(batch => batch.Cases).Select(row => row.Id).Order());
        Assert.All(all.SelectMany(batch => batch.Cases.Select(row => (row, batch))).GroupBy(pair => pair.row.FullyQualifiedName),
            group => Assert.Single(group.Select(pair => (pair.row.Shard, pair.batch.Id)).Distinct()));
        for (var shard = 0; shard < 4; shard++)
        {
            var fast = ExecutionScheduling.Batches(fixture.Plan, shard, "fast").SelectMany(batch => batch.Cases).Select(row => row.Id).ToHashSet();
            var heavy = ExecutionScheduling.Batches(fixture.Plan, shard, "heavy").SelectMany(batch => batch.Cases).Select(row => row.Id).ToHashSet();
            Assert.Empty(fast.Intersect(heavy));
            Assert.Equal(fixture.Plan.Cases.Count(row => row.Shard == shard), fast.Union(heavy).Count());
            Assert.All(ExecutionScheduling.Batches(fixture.Plan, shard).Where(batch => batch.Lane == "exclusive"), batch => Assert.Equal("exclusive-0", batch.Id));
        }
    }

    [Fact]
    public void ReviewedBankTheorySplitsWithoutSeparatingCaseInsensitiveNames()
    {
        using var fixture = new Fixture();
        var method = ExecutionScheduling.SplitBankScopeTheory;
        var names = new[] { "bank A", "BANK a", "bank B", "bank C", "bank D", "bank E" };
        var cases = names.Select(name => new PlannedCase(Guid.NewGuid(), method, name, 0)).ToArray();
        var profile = fixture.Plan.Execution! with { ReviewedMethods = [method] };
        var plan = fixture.Plan with
        {
            Cases = cases,
            Execution = profile,
            Timings = [new MethodTiming(method, 2 * TimeSpan.TicksPerSecond, cases.Length)]
        };
        var batches = ExecutionScheduling.Batches(plan, 0, batchCount: 2);
        Assert.Equal(cases.Select(row => row.Id).Order(), batches.SelectMany(batch => batch.Cases).Select(row => row.Id).Order());
        Assert.Equal(2, batches.Length);
        Assert.Single(batches, batch => batch.Cases.Any(row => row.DisplayName == "bank A")
            && batch.Cases.Any(row => row.DisplayName == "BANK a"));
        var excluded = plan with { Execution = profile with { ExclusiveMethods = [method] } };
        Assert.Single(ExecutionScheduling.Batches(excluded, 0));
        Assert.Equal("exclusive", ExecutionScheduling.Batches(excluded, 0)[0].Lane);
        var unreviewed = plan with { Execution = profile with { ReviewedMethods = [] } };
        Assert.Single(ExecutionScheduling.Batches(unreviewed, 0));
    }

    [Fact]
    public void BankRowFilterEscapesOperatorsAndKeepsOtherMethodsWhole()
    {
        using var fixture = new Fixture();
        var method = ExecutionScheduling.SplitBankScopeTheory;
        var name = @"row (a)&b|c=d!e~f\g";
        var path = Path.Combine(fixture.Root, "bank.runsettings");
        ShardPlan.WriteRunSettings(path, [new(Guid.NewGuid(), method, name, 0),
            new(Guid.NewGuid(), "Tests.Case.Other", "ignored", 0)]);
        var filter = XDocument.Load(path).Root!.Element("RunConfiguration")!.Element("TestCaseFilter")!.Value;
        Assert.Contains(@"DisplayName=row \(a\)\&b\|c\=d\!e\~f\\g", filter);
        Assert.Contains("FullyQualifiedName=Tests.Case.Other", filter);
    }

    [Fact]
    public void UnknownMethodsRemainExclusiveEvenWhenCachedTimingsCallThemFast()
    {
        using var fixture = new Fixture();
        Assert.Equal("exclusive", ExecutionScheduling.Lane(fixture.Plan, "Tests.Case.Method31"));
        Assert.Equal("exclusive", ExecutionScheduling.Lane(fixture.Plan, "Tests.Case.Method0"));
        Assert.Equal("fast", ExecutionScheduling.Lane(fixture.Plan, "Tests.Case.Method2"));
        Assert.Equal("heavy", ExecutionScheduling.Lane(fixture.Plan, "Tests.Case.Method1"));
        var withoutTiming = fixture.Plan with { Timings = fixture.Plan.Timings!.Where(row => row.FullyQualifiedName != "Tests.Case.Method2").ToArray() };
        Assert.Equal("heavy", ExecutionScheduling.Lane(withoutTiming, "Tests.Case.Method2"));
        var threshold = fixture.Plan with
        {
            Timings = fixture.Plan.Timings!
                .Select(row => row.FullyQualifiedName == "Tests.Case.Method2" ? row with { AverageTicks = TimeSpan.TicksPerSecond } : row)
                .ToArray(),
        };
        Assert.Equal("heavy", ExecutionScheduling.Lane(threshold, "Tests.Case.Method2"));
        var exclusiveClass = fixture.Plan with { Execution = fixture.Plan.Execution! with { ExclusiveClasses = ["Tests.Case"] } };
        Assert.All(exclusiveClass.Cases, row => Assert.Equal("exclusive", ExecutionScheduling.Lane(exclusiveClass, row.FullyQualifiedName)));
    }

    [Fact]
    public void ReversedDiscoveryAndTimingsProduceTheSameBoundedBatches()
    {
        using var fixture = new Fixture();
        var reversed = ShardPlan.Create(fixture.Plan.Revision, fixture.Plan.AssemblyPath, fixture.Plan.Artifacts,
            fixture.Plan.Cases.Reverse().Select(row => new DiscoveredCase(row.Id, row.FullyQualifiedName, row.DisplayName)),
            4, fixture.Plan.Timings!.Reverse().ToArray(), fixture.Plan.Execution);
        for (var shard = 0; shard < 4; shard++)
            Assert.Equal(JsonSerializer.Serialize(ExecutionScheduling.Batches(fixture.Plan, shard)), JsonSerializer.Serialize(ExecutionScheduling.Batches(reversed, shard)));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("threshold")]
    [InlineData("batch-count")]
    [InlineData("duplicate")]
    [InlineData("injection")]
    [InlineData("unsorted")]
    public void InvalidExecutionPolicyFailsClosed(string scenario)
    {
        using var fixture = new Fixture();
        var profile = fixture.Plan.Execution!;
        profile = scenario switch
        {
            "schema" => profile with { SchemaVersion = 2 },
            "threshold" => profile with { FastThresholdTicks = 0 },
            "batch-count" => profile with { BatchCount = 0 },
            "duplicate" => profile with { ExclusiveMethods = ["Tests.Case.Method0", "Tests.Case.Method0"] },
            "injection" => profile with { ReviewedMethods = ["Tests.Case.Method|FullyQualifiedName~Other"] },
            _ => profile with { ReviewedMethods = ["Tests.Case.Z", "Tests.Case.A"] },
        };
        Assert.Throws<InvalidDataException>(() => ShardPlan.Validate(fixture.Plan with { Execution = profile }));
    }

    [Theory]
    [InlineData("auto", 12, 8, 4)]
    [InlineData("4", 12, 8, 4)]
    [InlineData("auto", 12, 4, 2)]
    [InlineData("auto", 8, 4, 2)]
    [InlineData("auto", 7, 8, 1)]
    [InlineData("4", 2, 8, 1)]
    [InlineData("2", 16, 8, 2)]
    [InlineData("1", 16, 8, 1)]
    public void WorkerBudgetUsesAvailableMemoryAndActualHostProcessors(string requested, int gib, int processors, int expected)
        => Assert.Equal(expected, ExecutionScheduling.WorkerBudget(requested, (ulong)gib * Fixture.Gib, processors));

    [Theory]
    [InlineData("auto", 6, 2)]
    [InlineData("auto", 8, 4)]
    [InlineData("auto", 2, 1)]
    [InlineData("1", 8, 1)]
    [InlineData("2", 8, 2)]
    [InlineData("4", 6, 2)]
    public void PartitionCapacityRespectsUserAndProcessorLimitsWithoutReservingMemory(string requested, int processors, int expected)
        => Assert.Equal(expected, ExecutionScheduling.PartitionCapacity(requested, processors));

    [Fact]
    public void MemoryAndConcurrencyBoundariesDoNotStartUnbudgetedWorkers()
    {
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.WorkerBudget("auto", Fixture.Gib - 1, 8));
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.WorkerBudget("3", 20 * Fixture.Gib, 8));
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.WorkerBudget("auto", 20 * Fixture.Gib, 0));
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.PartitionCapacity("3", 8));
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.PartitionCapacity("auto", 0));
        Assert.Equal(2, ExecutionScheduling.WorkerBudget("auto", 12 * Fixture.Gib - 1, 8));
        Assert.Equal(1, ExecutionScheduling.WorkerBudget("auto", 8 * Fixture.Gib - 1, 8));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(0, 4, Fixture.Gib));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(0, 4, Fixture.Gib - 1));
        Assert.True(ExecutionScheduling.CanLaunchAdditional(0, 4, 6 * Fixture.Gib));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(0, 4, 6 * Fixture.Gib - 1));
        Assert.True(ExecutionScheduling.CanLaunchAdditional(0, 4, 7 * Fixture.Gib, "exclusive"));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(0, 4, 7 * Fixture.Gib - 1, "exclusive"));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(1, 4, 20 * Fixture.Gib, "exclusive"));
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.CanLaunchAdditional(0, 1, 20 * Fixture.Gib, "unknown"));
        Assert.True(ExecutionScheduling.CanLaunchAdditional(1, 4, 6 * Fixture.Gib));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(1, 4, 6 * Fixture.Gib - 1));
        Assert.False(ExecutionScheduling.CanLaunchAdditional(4, 4, 100 * Fixture.Gib));
        foreach (var lane in new[] { "fast", "heavy", "exclusive" })
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DOTNET_GCHeapHardLimit"] = "200000000",
                ["DOTNET_GCHeapHardLimitPercent"] = "80",
                ["COMPlus_GCHeapHardLimitSOH"] = "180000000",
                ["COMPlus_GCHeapHardLimitLOHPercent"] = "90",
                ["Email__Enabled"] = "true",
                ["BackgroundServices__Enabled"] = "true",
                ["DOTNET_PROCESSOR_COUNT"] = "16",
                ["KEEP_EXISTING_SETTING"] = "unchanged",
            };
            ExecutionScheduling.ConfigureWorkerEnvironment(environment, lane);
            Assert.Equal(lane == "exclusive" ? "C0000000" : "80000000", environment["DOTNET_GCHeapHardLimit"]);
            Assert.Single(environment.Keys, key => key.Contains("GCHeapHardLimit", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("2", environment["DOTNET_PROCESSOR_COUNT"]);
            Assert.Equal("false", environment["Email__Enabled"]);
            Assert.Equal("false", environment["BackgroundServices__Enabled"]);
            Assert.Equal("unchanged", environment["KEEP_EXISTING_SETTING"]);
        }
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.ConfigureWorkerEnvironment(new Dictionary<string, string?>(), "unknown"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void FullQualificationRechecksEveryNativeBatchAndPreservesDurations(int partitions)
    {
        using var fixture = new Fixture(partitions: partitions);
        fixture.WriteAll("all");
        Assert.Equal(fixture.Plan.Cases.Length, TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root).Sum(row => row.Cases));
        var timings = TestTimings.FromQualifiedResults(fixture.Plan, fixture.Hash, fixture.Root);
        Assert.Equal(fixture.Plan.Cases.Length, timings.Methods.Sum(row => row.Cases));
        Assert.All(timings.Methods, row => Assert.Equal(TimeSpan.TicksPerSecond / 2, row.AverageTicks));
        var batch = ExecutionScheduling.Batches(fixture.Plan, 0, batchCount: partitions)[0];
        var path = Path.Combine(fixture.Root, "shard-0", "batches", batch.Id, "run.json");
        var invocation = JsonSerializer.Deserialize<BatchRun>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(invocation with { ExitCode = 17 }));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void UnsupportedPartitionCountsCannotAlterTheCatalog(int count)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.Batches(fixture.Plan, 0, batchCount: count));
        fixture.WriteAll("all");
        var path = Path.Combine(fixture.Root, "shard-0", "run.json");
        var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(run with { BatchCount = count }));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingOrAlteredPartitionReceiptCannotQualifyFourPartitionResults(int? count)
    {
        using var fixture = new Fixture();
        fixture.WriteAll("all");
        var path = Path.Combine(fixture.Root, "shard-0", "run.json");
        var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(run with { BatchCount = count }));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("skipped")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra-directory")]
    [InlineData("wrong-method")]
    [InlineData("wrong-batch")]
    public void MergeRejectsIncompleteAlteredOrUnsuccessfulBatches(string scenario)
    {
        using var fixture = new Fixture();
        var directory = fixture.WriteBatches(0, "all");
        var batch = ExecutionScheduling.Batches(fixture.Plan, 0)[0];
        var root = Path.Combine(directory, "batches", batch.Id);
        if (scenario is "native" or "wrong-batch")
        {
            var path = Path.Combine(root, "run.json");
            var invocation = JsonSerializer.Deserialize<BatchRun>(File.ReadAllText(path))!;
            File.WriteAllText(path, JsonSerializer.Serialize(scenario == "native" ? invocation with { ExitCode = 1 } : invocation with { Batch = "heavy-9" }));
        }
        else if (scenario == "extra-directory") Directory.CreateDirectory(Path.Combine(directory, "batches", "unplanned-0"));
        else
        {
            var path = Path.Combine(root, "results.trx");
            var document = XDocument.Load(path);
            var row = document.Descendants(Fixture.Ns + "UnitTestResult").First();
            if (scenario == "skipped") row.SetAttributeValue("outcome", "NotExecuted");
            if (scenario == "missing") row.Remove();
            if (scenario == "duplicate") row.AddAfterSelf(new XElement(row));
            if (scenario == "wrong-method") document.Descendants(Fixture.Ns + "TestMethod").First().SetAttributeValue("name", "Unplanned");
            document.Save(path);
        }
        Assert.Throws<InvalidDataException>(() => BatchResults.Merge(fixture.Plan, fixture.Hash, 0, directory, "all", Path.Combine(directory, "results.trx"), 4));
        Assert.False(File.Exists(Path.Combine(directory, "results.trx")));
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("heavy")]
    public void PartialSuitesHaveExactCoverageButCannotBecomeFullQualification(string suite)
    {
        using var fixture = new Fixture();
        fixture.WriteAll(suite);
        var selected = Enumerable.Range(0, 4).Select(shard => BatchResults.VerifySubset(fixture.Plan, fixture.Hash, shard,
            Path.Combine(fixture.Root, $"shard-{shard}"), suite)).ToArray();
        Assert.All(selected, row => { Assert.False(row.Qualified); Assert.True(row.SubsetPassed); });
        Assert.Equal(Enumerable.Range(0, 4).SelectMany(shard => ExecutionScheduling.Batches(fixture.Plan, shard, suite)).Sum(batch => batch.Cases.Length), selected.Sum(row => row.Cases));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root));
        Assert.Throws<InvalidDataException>(() => TestTimings.FromQualifiedResults(fixture.Plan, fixture.Hash, fixture.Root));
    }

    [Fact]
    public void AnEmptyPartialShardHasExplicitZeroCoverageAndCannotPassAsAFullRun()
    {
        using var fixture = new Fixture(allExclusive: true);
        fixture.WriteAll("fast");
        Assert.Empty(ExecutionScheduling.Batches(fixture.Plan, 0, "fast"));
        var result = BatchResults.VerifySubset(fixture.Plan, fixture.Hash, 0, Path.Combine(fixture.Root, "shard-0"), "fast");
        Assert.Equal(0, result.Cases);
        Assert.False(result.Qualified);
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(fixture.Plan, fixture.Hash, fixture.Root));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"parallelizeTestCollections\":true,\"maxParallelThreads\":1}")]
    [InlineData("{\"parallelizeTestCollections\":false,\"maxParallelThreads\":2}")]
    public void ASecondLayerOfXunitConcurrencyIsRejected(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "xunit.runner.json"), json);
        Assert.Throws<InvalidDataException>(() => ExecutionScheduling.RequireSerialXunit(fixture.Root));
        File.WriteAllText(Path.Combine(fixture.Root, "xunit.runner.json"), "{\"parallelizeTestCollections\":false,\"maxParallelThreads\":1}");
        ExecutionScheduling.RequireSerialXunit(fixture.Root);
    }

    private sealed class Fixture : IDisposable
    {
        public const ulong Gib = 1024UL * 1024 * 1024;
        public static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pp-bounded-tests-" + Guid.NewGuid().ToString("N"));
        public TestPlan Plan { get; }
        public string Hash { get; } = new('a', 64);
        private int Partitions { get; }

        public Fixture(bool allExclusive = false, int partitions = 4)
        {
            Partitions = partitions;
            Directory.CreateDirectory(Root);
            var methods = Enumerable.Range(0, 32).Select(index => "Tests.Case.Method" + index).ToArray();
            var cases = methods.SelectMany(method => Enumerable.Range(0, 2).Select(index => new DiscoveredCase(Guid.NewGuid(), method, method + " row " + index)));
            var timings = methods.Select((method, index) => new MethodTiming(method, index % 2 == 0 || index == 31
                ? TimeSpan.TicksPerSecond / 2 : TimeSpan.TicksPerSecond * 2, 2)).ToArray();
            var profile = new ExecutionProfile(1, TimeSpan.TicksPerSecond, 4, [], [methods[0]],
                allExclusive ? [] : methods.Where(method => method != "Tests.Case.Method31").Order(StringComparer.Ordinal).ToArray());
            Plan = ShardPlan.Create(new string('b', 40), "tests.dll", [new ArtifactPin("tests.dll", new string('c', 64))], cases, 4, timings, profile);
        }

        public string WriteBatches(int shard, string suite)
        {
            var directory = Path.Combine(Root, $"shard-{shard}");
            Directory.CreateDirectory(Path.Combine(directory, "batches"));
            foreach (var batch in ExecutionScheduling.Batches(Plan, shard, suite, Partitions))
            {
                var root = Path.Combine(directory, "batches", batch.Id);
                Directory.CreateDirectory(root);
                var now = DateTime.UtcNow;
                File.WriteAllText(Path.Combine(root, "run.json"), JsonSerializer.Serialize(new BatchRun(1, Plan.Revision, Hash, shard,
                    batch.Id, batch.Lane, Plan.Artifacts[0].Sha256, now, now, 0, null)));
                var rows = batch.Cases;
                var counters = new XElement(Ns + "Counters", new XAttribute("total", rows.Length), new XAttribute("executed", rows.Length), new XAttribute("passed", rows.Length));
                foreach (var name in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending" }) counters.Add(new XAttribute(name, 0));
                new XDocument(new XElement(Ns + "TestRun",
                    new XElement(Ns + "Results", rows.Select(row => new XElement(Ns + "UnitTestResult", new XAttribute("testId", row.Id),
                        new XAttribute("executionId", Guid.NewGuid()), new XAttribute("testName", row.DisplayName), new XAttribute("duration", "00:00:00.5000000"), new XAttribute("outcome", "Passed")))),
                    new XElement(Ns + "TestDefinitions", rows.Select(row => new XElement(Ns + "UnitTest", new XAttribute("id", row.Id), new XAttribute("name", row.DisplayName),
                        new XElement(Ns + "TestMethod", new XAttribute("className", row.FullyQualifiedName[..row.FullyQualifiedName.LastIndexOf('.')]), new XAttribute("name", row.FullyQualifiedName[(row.FullyQualifiedName.LastIndexOf('.') + 1)..]))))),
                    new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Completed"), counters))).Save(Path.Combine(root, "results.trx"));
            }
            return directory;
        }

        public void WriteAll(string suite)
        {
            for (var shard = 0; shard < 4; shard++)
            {
                var directory = WriteBatches(shard, suite);
                BatchResults.Merge(Plan, Hash, shard, directory, suite, Path.Combine(directory, suite == "all" ? "results.trx" : "subset-results.trx"), Partitions);
                var now = DateTime.UtcNow;
                File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(new ShardRun(1, Plan.Revision, Hash,
                    shard, Plan.Artifacts[0].Sha256, now, now, 0, null, suite, Partitions)));
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
