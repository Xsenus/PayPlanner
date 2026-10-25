using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PayPlanner.TestShards;

public static class BatchResults
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static void Verify(TestPlan plan, string hash, int shard, ExecutionBatch batch, string directory)
    {
        var run = JsonSerializer.Deserialize<BatchRun>(File.ReadAllBytes(Path.Combine(directory, "run.json")), ShardPlan.Json)
            ?? throw new InvalidDataException("Missing batch invocation metadata.");
        ShardPlan.Require(run.SchemaVersion == 1 && run.Revision == plan.Revision && run.ManifestSha256 == hash
            && run.Shard == shard && run.Batch == batch.Id && run.Lane == batch.Lane
            && run.AssemblySha256 == plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256
            && run.ExitCode == 0 && run.ErrorKind is null && run.FinishedUtc >= run.StartedUtc,
            "The native batch did not complete successfully with the planned inputs.");
        TrxVerifier.VerifyCases(batch.Cases, directory);
    }

    public static ExecutionBatch[] VerifyAll(TestPlan plan, string hash, int shard, string directory, string suite, int? batchCount = null)
    {
        if (batchCount is null)
        {
            var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllBytes(Path.Combine(directory, "run.json")), ShardPlan.Json)
                ?? throw new InvalidDataException("Missing bounded invocation metadata.");
            batchCount = run.BatchCount;
            ShardPlan.Require(batchCount is not null, "The bounded invocation must pin its partition count.");
        }
        var batches = ExecutionScheduling.Batches(plan, shard, suite, batchCount);
        var root = Path.Combine(directory, "batches");
        ShardPlan.Require(Directory.Exists(root), "The execution batch directory is missing.");
        ShardPlan.Require(Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .SequenceEqual(batches.Select(row => row.Id).Order(StringComparer.Ordinal)), "The batch directory union does not match its exact plan.");
        foreach (var batch in batches) Verify(plan, hash, shard, batch, Path.Combine(root, batch.Id));
        return batches;
    }

    public static void Merge(TestPlan plan, string hash, int shard, string directory, string suite, string destination, int? batchCount = null)
    {
        var batches = VerifyAll(plan, hash, shard, directory, suite, batchCount);
        ShardPlan.Require(!File.Exists(destination), "Preserve an existing merged TRX.");
        var results = new XElement(Ns + "Results");
        var definitions = new XElement(Ns + "TestDefinitions");
        var entries = new XElement(Ns + "TestEntries");
        XElement? lists = null;
        foreach (var batch in batches)
        {
            using var reader = XmlReader.Create(Path.Combine(directory, "batches", batch.Id, "results.trx"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var source = XDocument.Load(reader).Root!;
            results.Add(source.Element(Ns + "Results")!.Elements().Select(row => new XElement(row)));
            definitions.Add(source.Element(Ns + "TestDefinitions")!.Elements().Select(row => new XElement(row)));
            entries.Add(source.Element(Ns + "TestEntries")?.Elements().Select(row => new XElement(row)));
            lists ??= source.Element(Ns + "TestLists") is { } value ? new XElement(value) : null;
        }
        var cases = batches.SelectMany(row => row.Cases).ToArray();
        var counters = new XElement(Ns + "Counters", new XAttribute("total", cases.Length),
            new XAttribute("executed", cases.Length), new XAttribute("passed", cases.Length));
        foreach (var name in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                     "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending" }) counters.Add(new XAttribute(name, 0));
        new XDocument(new XElement(Ns + "TestRun", new XAttribute("id", Guid.NewGuid()),
            new XAttribute("name", $"Qualified backend shard {shard}, suite {suite}"), results, definitions, entries, lists,
            new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Completed"), counters))).Save(destination);
        TrxVerifier.VerifyCases(cases, Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
    }

    public static SubsetQualification VerifySubset(TestPlan plan, string hash, int shard, string directory, string suite)
    {
        ShardPlan.Require(suite is "fast" or "heavy", "Only an explicit partial suite may use subset qualification.");
        var run = JsonSerializer.Deserialize<ShardRun>(File.ReadAllBytes(Path.Combine(directory, "run.json")), ShardPlan.Json)
            ?? throw new InvalidDataException("Missing subset invocation metadata.");
        ShardPlan.Require(run.SchemaVersion == ShardPlan.SchemaVersion && run.Suite == suite && run.Shard == shard
            && run.Revision == plan.Revision && run.ManifestSha256 == hash && run.ExitCode == 0 && run.ErrorKind is null
            && run.AssemblySha256 == plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256
            && run.FinishedUtc >= run.StartedUtc, "The subset invocation did not complete successfully.");
        var batches = VerifyAll(plan, hash, shard, directory, suite);
        var cases = batches.SelectMany(row => row.Cases).ToArray();
        TrxVerifier.VerifyCases(cases, directory, "subset-results.trx");
        ShardPlan.Require(!File.Exists(Path.Combine(directory, "qualification.json")) && !File.Exists(Path.Combine(directory, "results.trx")),
            "A partial suite must not publish full qualification artifacts.");
        return new(false, true, suite, shard, cases.Length, hash, plan.Revision);
    }
}
