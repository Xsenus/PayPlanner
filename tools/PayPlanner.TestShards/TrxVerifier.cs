using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace PayPlanner.TestShards;

public sealed record ShardQualification(bool Qualified, int Shard, int Cases, string ManifestSha256, string Revision);

public static class TrxVerifier
{
    private const string Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static ShardQualification Verify(TestPlan plan, string manifestHash, int shard, string directory)
    {
        ShardPlan.Require(shard >= 0 && shard < plan.ShardCount, "Invalid shard index.");
        var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllBytes(Path.Combine(directory, "run.json")), ShardPlan.Json)
            ?? throw new InvalidDataException("Missing shard run metadata.");
        ShardPlan.Require(run.SchemaVersion == ShardPlan.SchemaVersion && run.Shard == shard && run.Revision == plan.Revision
            && string.Equals(run.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(run.AssemblySha256, plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256, StringComparison.OrdinalIgnoreCase)
            && run.ExitCode == 0 && run.ErrorKind is null && run.FinishedUtc >= run.StartedUtc, "Shard invocation did not complete successfully with the planned artifact.");
        var cases = plan.Cases.Where(row => row.Shard == shard).ToArray();
        VerifyCases(cases, directory);
        return new(true, shard, cases.Length, manifestHash, plan.Revision);
    }

    public static ShardQualification VerifyRestored(TestPlan plan, string manifestHash, string directory, int exitCode)
    {
        ShardPlan.Require(exitCode == 0, "The native restored-browser run failed.");
        var cases = RestoredCases(plan);
        VerifyCases(cases, directory);
        return new(true, -1, cases.Length, manifestHash, plan.Revision);
    }

    public static PlannedCase[] RestoredCases(TestPlan plan)
    {
        const string protectedSettings = "PayPlanner.Api.Tests.DatabaseBackupServiceS3Tests.RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing";
        const string managedGenerations = "PayPlanner.Api.Tests.DatabaseRestoreDrillEndToEndPostgreSqlTests.FullModelBackupRestoresReadableManagedGenerationsWithoutSource";
        var cases = plan.Cases.Where(row => row.FullyQualifiedName is protectedSettings or managedGenerations).ToArray();
        ShardPlan.Require(cases.Length == 5 && cases.Count(row => row.FullyQualifiedName == protectedSettings) == 1
            && cases.Count(row => row.FullyQualifiedName == managedGenerations) == 4, "The restored-browser manifest must contain its exact five cases.");
        return cases;
    }

    private static void VerifyCases(PlannedCase[] cases, string directory)
    {
        var expected = cases.ToDictionary(row => row.Id);
        var definitions = new Dictionary<Guid, (string Name, string Method)>();
        var results = new Dictionary<Guid, string>();
        var executions = new HashSet<Guid>();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        var rootCount = 0;
        var summaries = 0;
        var counterNodes = 0;
        var section = "";
        var sections = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(Path.Combine(directory, "results.trx"), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1) section = "";
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth == 0)
            {
                ShardPlan.Require(reader.LocalName == "TestRun" && reader.NamespaceURI == Namespace, "Invalid TRX root.");
                rootCount++;
            }
            if (reader.NamespaceURI != Namespace) continue;
            if (reader.Depth == 1)
            {
                section = reader.LocalName;
                sections[reader.LocalName] = sections.GetValueOrDefault(reader.LocalName) + 1;
            }
            if (reader.LocalName == "UnitTestResult")
            {
                ShardPlan.Require(reader.Depth == 2 && section == "Results", "Unexpected result nesting.");
                var id = Id(reader, "testId");
                ShardPlan.Require(expected.ContainsKey(id), "Unplanned test result.");
                ShardPlan.Require(reader.GetAttribute("outcome") == "Passed", "A test did not pass.");
                ShardPlan.Require(executions.Add(Id(reader, "executionId")), "Duplicate execution result.");
                ShardPlan.Require(results.TryAdd(id, Required(reader, "testName")), "Duplicate test result or deferred theory.");
            }
            else if (reader.LocalName == "UnitTest")
            {
                ShardPlan.Require(reader.Depth == 2 && section == "TestDefinitions", "Unexpected definition nesting.");
                var id = Id(reader, "id");
                var name = Required(reader, "name");
                var methods = new List<string>();
                using (var subtree = reader.ReadSubtree())
                    while (subtree.Read())
                        if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "TestMethod" && subtree.NamespaceURI == Namespace)
                            methods.Add(Required(subtree, "className") + "." + Required(subtree, "name"));
                ShardPlan.Require(methods.Count == 1 && definitions.TryAdd(id, (name, methods[0])), "Invalid or duplicate test definition.");
            }
            else if (reader.LocalName == "ResultSummary")
            {
                summaries++;
                ShardPlan.Require(reader.Depth == 1 && reader.GetAttribute("outcome") == "Completed", "TRX run did not complete successfully.");
            }
            else if (reader.LocalName == "Counters")
            {
                counterNodes++;
                ShardPlan.Require(reader.Depth == 2 && section == "ResultSummary", "Unexpected counters nesting.");
                if (reader.MoveToFirstAttribute())
                    do
                    {
                        ShardPlan.Require(long.TryParse(reader.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0, "Invalid result counter.");
                        counters.Add(reader.LocalName, value);
                    } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            else if (reader.LocalName == "RunInfo")
                ShardPlan.Require(reader.GetAttribute("outcome") is "Passed" or "Completed", "Run-level error or warning, including fixture failure.");
            else if (reader.LocalName == "ErrorInfo")
                throw new InvalidDataException("TRX contains error information.");
        }
        ShardPlan.Require(rootCount == 1 && summaries == 1 && counterNodes == 1
            && sections.GetValueOrDefault("Results") == 1 && sections.GetValueOrDefault("TestDefinitions") == 1, "Incomplete TRX structure.");
        ShardPlan.Require(results.Count == expected.Count && definitions.Count == expected.Count, "Missing test cases or unexpected definitions.");
        foreach (var (id, row) in expected)
            ShardPlan.Require(results.TryGetValue(id, out var display) && display == row.DisplayName
                && definitions.TryGetValue(id, out var definition) && definition.Name == row.DisplayName
                && definition.Method == row.FullyQualifiedName, "Executed test identity does not match discovery.");
        foreach (var name in new[] { "total", "executed", "passed" })
            ShardPlan.Require(counters.TryGetValue(name, out var value) && value == expected.Count, "TRX totals do not cover the plan.");
        foreach (var name in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending" })
            ShardPlan.Require(counters.TryGetValue(name, out var value) && value == 0, "Skipped, failed or unfinished tests are not allowed.");
    }

    public static ShardQualification[] VerifyAll(TestPlan plan, string manifestHash, string root)
    {
        ShardPlan.Validate(plan);
        var directories = Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        ShardPlan.Require(directories.SequenceEqual(Enumerable.Range(0, plan.ShardCount).Select(index => $"shard-{index}")), "The aggregate must contain exactly four shard directories.");
        var qualifications = Enumerable.Range(0, plan.ShardCount)
            .Select(shard => Verify(plan, manifestHash, shard, Path.Combine(root, $"shard-{shard}"))).ToArray();
        ShardPlan.Require(qualifications.Sum(row => row.Cases) == plan.Cases.Length, "Aggregate coverage mismatch.");
        return qualifications;
    }

    private static Guid Id(XmlReader reader, string name)
    {
        ShardPlan.Require(Guid.TryParse(Required(reader, name), out var value) && value != Guid.Empty, "Invalid test ID.");
        return value;
    }

    private static string Required(XmlReader reader, string name)
        => reader.GetAttribute(name) ?? throw new InvalidDataException("Missing TRX attribute.");
}
