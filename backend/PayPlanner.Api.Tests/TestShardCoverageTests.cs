using System.Text.Json;
using System.Xml.Linq;
using PayPlanner.TestShards;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TestShardCoverageTests
{
    [Fact]
    public void TimingPlanSeparatesSlowMethodsWithoutLosingTheoryRowsOrNewMethods()
    {
        using var f = new Fixture();
        var cases = Enumerable.Range(0, 16).Select(index => new DiscoveredCase(Guid.NewGuid(),
            "Tests.Duration.Method" + index / 2, "row " + index)).ToArray();
        var timings = Enumerable.Range(0, 8).Select(index => new MethodTiming("Tests.Duration.Method" + index,
            index is 0 or 4 ? 900 : 10, 2)).ToArray();
        var plain = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, cases, 4);
        var timed = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, cases, 4, timings);
        decimal Longest(TestPlan plan) => plan.Cases.GroupBy(row => row.Shard)
            .Max(group => group.Sum(row => (decimal)timings.Single(t => t.FullyQualifiedName == row.FullyQualifiedName).AverageTicks));
        Assert.True(Longest(timed) < Longest(plain));
        Assert.Equal(cases.Select(row => row.Id).Order(), timed.Cases.Select(row => row.Id).Order());
        Assert.All(timed.Cases.GroupBy(row => row.FullyQualifiedName), group => Assert.Single(group.Select(row => row.Shard).Distinct()));
        var reversed = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, cases.Reverse(), 4, timings.Reverse().ToArray());
        Assert.Equal(JsonSerializer.Serialize(timed), JsonSerializer.Serialize(reversed));
        ShardPlan.Validate(timed);
        var newCase = new DiscoveredCase(Guid.NewGuid(), "Tests.Duration.Unknown", "new theory row");
        var expanded = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, cases.Append(newCase), 4, timings);
        Assert.Contains(expanded.Cases, row => row.Id == newCase.Id);
        Assert.Equal(cases.Length + 1, expanded.Cases.Length);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("no-cases")]
    [InlineData("duplicate")]
    [InlineData("filter-injection")]
    public void InvalidTimingProfileCannotChangeTheTestPlan(string scenario)
    {
        using var f = new Fixture();
        var row = new MethodTiming(f.Cases[0].FullyQualifiedName, 100, 2);
        var timings = scenario switch
        {
            "negative" => new[] { row with { AverageTicks = -1 } },
            "zero" => new[] { row with { AverageTicks = 0 } },
            "no-cases" => new[] { row with { Cases = 0 } },
            "duplicate" => new[] { row, row },
            "filter-injection" => new[] { row with { FullyQualifiedName = "Tests.Method|FullyQualifiedName~Other" } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Throws<InvalidDataException>(() => ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, f.Cases, 4, timings));
    }

    [Fact]
    public void TimingsRequireACompleteQualifiedRunAndPreserveAllCaseCounts()
    {
        using var f = new Fixture();
        f.WriteAll();
        var profile = TestTimings.FromQualifiedResults(f.Plan, f.ManifestHash, f.Root);
        Assert.Equal(f.Plan.Revision, profile.Revision);
        Assert.Equal(f.ManifestHash, profile.ManifestSha256);
        Assert.Equal(f.Cases.Length, profile.Methods.Sum(row => row.Cases));
        Assert.All(profile.Methods, row => Assert.Equal(TimeSpan.FromSeconds(0.5).Ticks, row.AverageTicks));
        var path = Path.Combine(f.Root, "shard-0", "results.trx");
        var document = XDocument.Load(path);
        document.Descendants(Fixture.Ns + "UnitTestResult").First().SetAttributeValue("outcome", "NotExecuted");
        document.Save(path);
        Assert.Throws<InvalidDataException>(() => TestTimings.FromQualifiedResults(f.Plan, f.ManifestHash, f.Root));
    }

    [Fact]
    public void TimingPlanQualifiesOnlyItsExactUnionAndRejectsAnAlteredAssignment()
    {
        using var f = new Fixture(weighted: true);
        f.WriteAll();
        Assert.Equal(f.Cases.Length, TrxVerifier.VerifyAll(f.Plan, f.ManifestHash, f.Root).Sum(row => row.Cases));
        var rows = f.Plan.Cases.ToArray();
        rows[0] = rows[0] with { Shard = (rows[0].Shard + 1) % 4 };
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(f.Plan with { Cases = rows }, f.ManifestHash, f.Root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-00:00:01")]
    [InlineData("NaN")]
    public void MissingOrInvalidDurationCannotProduceATimingBaseline(string? duration)
    {
        using var f = new Fixture();
        f.WriteAll();
        var path = Path.Combine(f.Root, "shard-0", "results.trx");
        var document = XDocument.Load(path);
        document.Descendants(Fixture.Ns + "UnitTestResult").First().SetAttributeValue("duration", duration);
        document.Save(path);
        Assert.Throws<InvalidDataException>(() => TestTimings.FromQualifiedResults(f.Plan, f.ManifestHash, f.Root));
    }

    [Fact]
    public void PlanKeepsEveryTheoryRowWithItsMethodAndUsesFourNonemptyShards()
    {
        using var f = new Fixture();
        Assert.Equal(f.Cases.Select(row => row.Id).Order(), f.Plan.Cases.Select(row => row.Id).Order());
        Assert.All(f.Plan.Cases.GroupBy(row => row.FullyQualifiedName), group => Assert.Single(group.Select(row => row.Shard).Distinct()));
        Assert.Equal([0, 1, 2, 3], f.Plan.Cases.Select(row => row.Shard).Distinct().Order().ToArray());
    }

    [Fact]
    public void ReversedDiscoveryProducesExactlyTheSameDeterministicManifest()
    {
        using var f = new Fixture();
        var reversed = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, f.Cases.Reverse(), 4);
        Assert.Equal(JsonSerializer.Serialize(f.Plan), JsonSerializer.Serialize(reversed));
    }

    [Fact]
    public void PreviouslyUnknownMethodIsIncludedWithoutAnAllowlist()
    {
        using var f = new Fixture();
        var added = new DiscoveredCase(Guid.NewGuid(), "Tests.NewClass.NewMethod", "Tests.NewClass.NewMethod");
        var plan = ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, f.Cases.Append(added), 4);
        Assert.Contains(plan.Cases, row => row.Id == added.Id);
        Assert.Equal(f.Cases.Length + 1, plan.Cases.Length);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("empty-id")]
    [InlineData("filter-injection")]
    [InlineData("empty-name")]
    public void InvalidDiscoveredIdentityCannotProduceAPlan(string scenario)
    {
        using var f = new Fixture();
        var rows = f.Cases.ToArray();
        rows[1] = scenario switch
        {
            "duplicate-id" => rows[1] with { Id = rows[0].Id },
            "empty-id" => rows[1] with { Id = Guid.Empty },
            "filter-injection" => rows[1] with { FullyQualifiedName = "Tests.Method|FullyQualifiedName~Other" },
            "empty-name" => rows[1] with { DisplayName = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Throws<InvalidDataException>(() => ShardPlan.Create(f.Plan.Revision, "tests.dll", f.Plan.Artifacts, rows, 4));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("outside-artifact")]
    [InlineData("absolute-artifact")]
    [InlineData("hash")]
    [InlineData("shard-count")]
    public void InvalidPlanBoundariesFailClosed(string scenario)
    {
        using var f = new Fixture();
        var revision = scenario == "revision" ? "main" : f.Plan.Revision;
        var path = scenario switch { "outside-artifact" => "../tests.dll", "absolute-artifact" => "/tests.dll", _ => "tests.dll" };
        var pins = new[] { new ArtifactPin(path, scenario == "hash" ? "short" : f.Plan.Artifacts[0].Sha256) };
        Assert.Throws<InvalidDataException>(() => ShardPlan.Create(revision, path, pins, f.Cases, scenario == "shard-count" ? 3 : 4));
    }

    [Fact]
    public void ManifestByteChangeDoesNotPassTheProducerHash()
    {
        using var f = new Fixture();
        File.AppendAllText(f.Manifest, " ");
        Assert.Throws<InvalidDataException>(() => ShardPlan.Read(f.Manifest, f.ManifestHash));
    }

    [Fact]
    public void ReplacedBinaryDoesNotPassArtifactVerification()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "tests.dll"), "different assembly bytes");
        Assert.Throws<InvalidDataException>(() => ShardPlan.VerifyArtifacts(f.Plan, f.Manifest));
    }

    [Fact]
    public void MethodCannotBeSplitByAnEditedPlan()
    {
        using var f = new Fixture();
        var rows = f.Plan.Cases.ToArray();
        rows[0] = rows[0] with { Shard = (rows[0].Shard + 1) % 4 };
        Assert.Throws<InvalidDataException>(() => ShardPlan.Validate(f.Plan with { Cases = rows }));
    }

    [Fact]
    public void RunSettingsContainsAllAndOnlyAssignedMethods()
    {
        using var f = new Fixture();
        var file = Path.Combine(f.Root, "test.runsettings");
        ShardPlan.WriteRunSettings(file, f.Plan, 0);
        var xml = XDocument.Load(file);
        Assert.Equal(ShardPlan.Filter(f.Plan, 0), xml.Root!.Element("RunConfiguration")!.Element("TestCaseFilter")!.Value);
        Assert.Equal("true", xml.Root.Element("RunConfiguration")!.Element("TreatNoTestsAsError")!.Value);
        Assert.All(ShardPlan.Filter(f.Plan, 0).Split('|'), term =>
            Assert.Contains(f.Plan.Cases, row => row.Shard == 0 && term == "FullyQualifiedName=" + row.FullyQualifiedName));
    }

    [Theory]
    [InlineData("Xunit.CollectionAttribute")]
    [InlineData("Xunit.CollectionDefinitionAttribute")]
    [InlineData("Xunit.CollectionBehaviorAttribute")]
    public void UnsupportedExplicitCollectionMetadataIsRejected(string attribute)
        => Assert.True(AssemblyCatalog.IsUnsupportedCollectionAttribute(attribute));

    [Fact]
    public void AFactIsAllowedButAnUnexpandedTheoryIsRejected()
    {
        var row = new DiscoveredCase(Guid.NewGuid(), "Tests.Case.Method", "Tests.Case.Method");
        AssemblyCatalog.RequireEnumeratedTheories([row], new HashSet<string>());
        Assert.Throws<InvalidDataException>(() => AssemblyCatalog.RequireEnumeratedTheories([row], new HashSet<string> { row.FullyQualifiedName }));
        AssemblyCatalog.RequireEnumeratedTheories([row with { DisplayName = row.DisplayName + "(value: 1)" }], new HashSet<string> { row.FullyQualifiedName });
    }

    [Fact]
    public void FourSuccessfulShardsQualifyTheExactUnion()
    {
        using var f = new Fixture();
        f.WriteAll();
        var result = TrxVerifier.VerifyAll(f.Plan, f.ManifestHash, f.Root);
        Assert.Equal(4, result.Length);
        Assert.All(result, row => Assert.True(row.Qualified));
        Assert.Equal(f.Cases.Length, result.Sum(row => row.Cases));
    }

    [Fact]
    public void AnAdditionalShardDirectoryCannotBeSilentlyIgnored()
    {
        using var f = new Fixture();
        f.WriteAll();
        Directory.CreateDirectory(Path.Combine(f.Root, "shard-4"));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyAll(f.Plan, f.ManifestHash, f.Root));
    }

    [Theory]
    [InlineData("missing-protected-settings")]
    [InlineData("missing-provider-case")]
    [InlineData("extra-provider-case")]
    public void RestoredGateRequiresItsExactFiveManifestCases(string scenario)
    {
        using var f = new Fixture();
        var cases = RestoredRows().ToList();
        if (scenario == "missing-protected-settings") cases.RemoveAt(0);
        if (scenario == "missing-provider-case") cases.RemoveAt(1);
        if (scenario == "extra-provider-case") cases.Add(cases[1] with { Id = Guid.NewGuid() });
        Assert.Throws<InvalidDataException>(() => TrxVerifier.RestoredCases(f.Plan with { Cases = cases.ToArray() }));
    }

    [Fact]
    public void RestoredGateSelectsOnlyItsManifestCasesAndHonorsNativeExit()
    {
        using var f = new Fixture();
        var expected = RestoredRows();
        var plan = f.Plan with { Cases = f.Plan.Cases.Concat(expected).ToArray() };
        Assert.Equal(expected, TrxVerifier.RestoredCases(plan));
        Assert.Throws<InvalidDataException>(() => TrxVerifier.VerifyRestored(plan, f.ManifestHash, f.Root, 1));
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("extra-row")]
    [InlineData("duplicate-row")]
    [InlineData("wrong-display")]
    [InlineData("wrong-method")]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("counter")]
    [InlineData("incomplete-summary")]
    [InlineData("fixture-error")]
    [InlineData("passed-with-error-info")]
    [InlineData("wrong-revision")]
    [InlineData("wrong-manifest")]
    [InlineData("wrong-binary")]
    [InlineData("native-exit")]
    [InlineData("missing-trx")]
    [InlineData("truncated-trx")]
    [InlineData("dtd")]
    public void IncompleteOrAlteredShardCannotQualify(string scenario)
    {
        using var f = new Fixture();
        f.WriteAll();
        var directory = Path.Combine(f.Root, "shard-0");
        var file = Path.Combine(directory, "results.trx");
        var document = XDocument.Load(file);
        var ns = Fixture.Ns;
        var first = document.Root!.Element(ns + "Results")!.Elements().First();
        if (scenario == "missing-row") first.Remove();
        if (scenario == "extra-row") first.SetAttributeValue("testId", Guid.NewGuid());
        if (scenario == "duplicate-row") document.Root.Element(ns + "Results")!.Add(new XElement(first));
        if (scenario == "wrong-display") first.SetAttributeValue("testName", "different test");
        if (scenario == "wrong-method") document.Descendants(ns + "TestMethod").First().SetAttributeValue("name", "DifferentMethod");
        if (scenario == "skipped") first.SetAttributeValue("outcome", "NotExecuted");
        if (scenario == "failed") first.SetAttributeValue("outcome", "Failed");
        if (scenario == "counter") document.Descendants(ns + "Counters").Single().SetAttributeValue("total", 999);
        if (scenario == "incomplete-summary") document.Root.Element(ns + "ResultSummary")!.Remove();
        if (scenario == "fixture-error") document.Root.Element(ns + "ResultSummary")!.Add(new XElement(ns + "RunInfos", new XElement(ns + "RunInfo", new XAttribute("outcome", "Error"))));
        if (scenario == "passed-with-error-info") first.Add(new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "synthetic error"))));
        document.Save(file);
        var runPath = Path.Combine(directory, "run.json");
        var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllText(runPath))!;
        run = scenario switch
        {
            "wrong-revision" => run with { Revision = new string('b', 40) },
            "wrong-manifest" => run with { ManifestSha256 = new string('0', 64) },
            "wrong-binary" => run with { AssemblySha256 = new string('0', 64) },
            "native-exit" => run with { ExitCode = 1 },
            _ => run,
        };
        File.WriteAllText(runPath, JsonSerializer.Serialize(run));
        if (scenario == "missing-trx") File.Delete(file);
        if (scenario == "truncated-trx") File.WriteAllText(file, "<TestRun");
        if (scenario == "dtd") File.WriteAllText(file, "<!DOCTYPE TestRun [<!ENTITY secret SYSTEM 'file:///not-readable'>]><TestRun>&secret;</TestRun>");
        Assert.ThrowsAny<Exception>(() => TrxVerifier.VerifyAll(f.Plan, f.ManifestHash, f.Root));
    }

    private static PlannedCase[] RestoredRows()
        => Enumerable.Range(0, 5).Select(index =>
        {
            var method = index == 0
                ? "PayPlanner.Api.Tests.DatabaseBackupServiceS3Tests.RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing"
                : "PayPlanner.Api.Tests.DatabaseRestoreDrillEndToEndPostgreSqlTests.FullModelBackupRestoresReadableManagedGenerationsWithoutSource";
            return new PlannedCase(Guid.NewGuid(), method, method + " case " + index, index % 4);
        }).ToArray();

    private sealed class Fixture : IDisposable
    {
        public static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pp-shard-coverage-" + Guid.NewGuid().ToString("N"));
        public string Manifest => Path.Combine(Root, "test-plan.json");
        public DiscoveredCase[] Cases { get; }
        public TestPlan Plan { get; }
        public string ManifestHash { get; }

        public Fixture(bool weighted = false)
        {
            Directory.CreateDirectory(Root);
            var assembly = Path.Combine(Root, "tests.dll");
            File.WriteAllText(assembly, "owned synthetic artifact");
            Cases = Enumerable.Range(0, 8).Select(index =>
            {
                var method = "Tests.Class.Method" + (index / 2);
                return new DiscoveredCase(Guid.NewGuid(), method, method + "(value: " + index + ")");
            }).ToArray();
            var timings = weighted ? Cases.GroupBy(row => row.FullyQualifiedName).Select((group, index) => new MethodTiming(group.Key, (index + 1) * 100, group.Count())).ToArray() : null;
            Plan = ShardPlan.Create(new string('a', 40), "tests.dll", [new("tests.dll", ShardPlan.HashFile(assembly))], Cases, 4, timings);
            File.WriteAllText(Manifest, JsonSerializer.Serialize(Plan));
            ManifestHash = ShardPlan.HashFile(Manifest);
        }

        public void WriteAll()
        {
            for (var shard = 0; shard < 4; shard++)
            {
                var directory = Path.Combine(Root, $"shard-{shard}");
                Directory.CreateDirectory(directory);
                var cases = Plan.Cases.Where(row => row.Shard == shard).ToArray();
                var now = DateTime.UtcNow;
                File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(new ShardRun(1, Plan.Revision, ManifestHash, shard, Plan.Artifacts[0].Sha256, now, now, 0, null)));
                var counters = new XElement(Ns + "Counters", new XAttribute("total", cases.Length), new XAttribute("executed", cases.Length), new XAttribute("passed", cases.Length));
                foreach (var name in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending" }) counters.Add(new XAttribute(name, 0));
                new XDocument(new XElement(Ns + "TestRun",
                    new XElement(Ns + "Results", cases.Select(row => new XElement(Ns + "UnitTestResult", new XAttribute("testId", row.Id), new XAttribute("executionId", Guid.NewGuid()), new XAttribute("testName", row.DisplayName), new XAttribute("duration", "00:00:00.5000000"), new XAttribute("outcome", "Passed")))),
                    new XElement(Ns + "TestDefinitions", cases.Select(row => new XElement(Ns + "UnitTest", new XAttribute("id", row.Id), new XAttribute("name", row.DisplayName),
                        new XElement(Ns + "TestMethod", new XAttribute("className", row.FullyQualifiedName[..row.FullyQualifiedName.LastIndexOf('.')]), new XAttribute("name", row.FullyQualifiedName[(row.FullyQualifiedName.LastIndexOf('.') + 1)..]))))),
                    new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Completed"), counters)))
                    .Save(Path.Combine(directory, "results.trx"));
            }
        }

        public void Dispose()
        {
            foreach (var directory in Directory.GetDirectories(Root))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory, recursive: false);
            }
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root, recursive: false);
        }
    }
}
