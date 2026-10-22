using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace PayPlanner.TestShards;

public sealed record MethodTiming(string FullyQualifiedName, long AverageTicks, int Cases);
public sealed record TimingProfile(int SchemaVersion, string Revision, string ManifestSha256, MethodTiming[] Methods);

/// <summary>Scheduling hints only. Qualification still checks every discovered identity and native result.</summary>
public static class TestTimings
{
    public static TimingProfile Read(string path)
    {
        var profile = JsonSerializer.Deserialize<TimingProfile>(File.ReadAllText(path), ShardPlan.Json)
            ?? throw new InvalidDataException("Empty timing profile.");
        ShardPlan.Require(profile.SchemaVersion == 1 && profile.Methods is { Length: > 0 }, "Unsupported or empty timing profile.");
        return profile;
    }

    public static TimingProfile FromQualifiedResults(TestPlan plan, string hash, string root)
    {
        // A partial, skipped, failed or altered run must never become a successful full baseline.
        _ = TrxVerifier.VerifyAll(plan, hash, root);
        var methods = plan.Cases.ToDictionary(row => row.Id, row => row.FullyQualifiedName);
        var totals = new Dictionary<string, (decimal Ticks, int Count)>(StringComparer.Ordinal);
        for (var shard = 0; shard < plan.ShardCount; shard++)
        {
            using var reader = XmlReader.Create(Path.Combine(root, $"shard-{shard}", "results.trx"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "UnitTestResult") continue;
                ShardPlan.Require(Guid.TryParse(reader.GetAttribute("testId"), out var id) && methods.ContainsKey(id), "Unknown timing test identity.");
                ShardPlan.Require(TimeSpan.TryParse(reader.GetAttribute("duration"), CultureInfo.InvariantCulture, out var duration)
                    && duration >= TimeSpan.Zero, "Missing or invalid test duration.");
                var method = methods[id];
                var previous = totals.GetValueOrDefault(method);
                totals[method] = (previous.Ticks + duration.Ticks, previous.Count + 1);
            }
        }
        ShardPlan.Require(totals.Values.Sum(row => row.Count) == plan.Cases.Length, "Incomplete timing coverage.");
        return new(1, plan.Revision, hash, totals.OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => new MethodTiming(row.Key, Math.Max(1L, (long)decimal.Ceiling(row.Value.Ticks / row.Value.Count)), row.Value.Count)).ToArray());
    }
}
