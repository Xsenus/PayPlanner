using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace PayPlanner.TestShards;

public sealed record DiscoveredCase(Guid Id, string FullyQualifiedName, string DisplayName);
public sealed record PlannedCase(Guid Id, string FullyQualifiedName, string DisplayName, int Shard);
public sealed record ArtifactPin(string Path, string Sha256);
public sealed record TestPlan(int SchemaVersion, string Revision, int ShardCount, string AssemblyPath,
    ArtifactPin[] Artifacts, PlannedCase[] Cases, MethodTiming[]? Timings = null);
public sealed record ShardRun(int SchemaVersion, string Revision, string ManifestSha256, int Shard,
    string AssemblySha256, DateTime StartedUtc, DateTime FinishedUtc, int ExitCode, string? ErrorKind);

public static partial class ShardPlan
{
    public const int SchemaVersion = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };

    public static TestPlan Create(string revision, string assemblyPath, ArtifactPin[] artifacts,
        IEnumerable<DiscoveredCase> discovered, int shardCount, MethodTiming[]? timings = null)
    {
        Require(Hex40().IsMatch(revision), "Revision must be an exact Git SHA.");
        Require(shardCount == 4, "The release gate requires four shards.");
        ValidateRelativePath(assemblyPath);
        Require(artifacts.Length > 0, "Artifact pins are missing.");
        Require(artifacts.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == artifacts.Length, "Duplicate artifact path.");
        foreach (var pin in artifacts)
        {
            ValidateRelativePath(pin.Path);
            Require(Hex64().IsMatch(pin.Sha256), "Invalid artifact hash.");
        }
        Require(artifacts.Count(pin => pin.Path == assemblyPath) == 1, "The test assembly must be pinned.");
        var cases = discovered.ToArray();
        Require(cases.Length > 0, "Discovery returned no tests.");
        Require(cases.Select(row => row.Id).Distinct().Count() == cases.Length, "Duplicate discovered test ID.");
        foreach (var row in cases)
        {
            Require(row.Id != Guid.Empty, "Empty test identity.");
            Require(MethodName().IsMatch(row.FullyQualifiedName), "Unsupported test method name in filter.");
            Require(!string.IsNullOrWhiteSpace(row.DisplayName), "Missing display name.");
        }
        if (timings is not null)
        {
            Require(timings.All(row => MethodName().IsMatch(row.FullyQualifiedName) && row.AverageTicks > 0 && row.Cases > 0), "Invalid timing estimate.");
            Require(timings.Select(row => row.FullyQualifiedName).Distinct(StringComparer.Ordinal).Count() == timings.Length, "Duplicate timing method.");
            var methods = cases.Select(row => row.FullyQualifiedName).ToHashSet(StringComparer.Ordinal);
            timings = timings.Where(row => methods.Contains(row.FullyQualifiedName)).OrderBy(row => row.FullyQualifiedName, StringComparer.Ordinal).ToArray();
        }
        var estimates = timings?.ToDictionary(row => row.FullyQualifiedName, row => row.AverageTicks, StringComparer.Ordinal);
        var fallback = timings is { Length: > 0 } ? timings.Select(row => row.AverageTicks).Order().ElementAt(timings.Length / 2) : 1L;
        decimal Weight(IGrouping<string, DiscoveredCase> group) => (decimal)(estimates?.GetValueOrDefault(group.Key, fallback) ?? 1L) * group.Count();
        var groups = cases.GroupBy(row => row.FullyQualifiedName, StringComparer.Ordinal)
            .OrderByDescending(Weight).ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        Require(groups.Length >= shardCount, "Too few methods for four nonempty shards.");
        var loads = new decimal[shardCount];
        var planned = new List<PlannedCase>(cases.Length);
        foreach (var group in groups)
        {
            var shard = Enumerable.Range(0, shardCount).OrderBy(index => loads[index]).ThenBy(index => index).First();
            foreach (var row in group) planned.Add(new(row.Id, row.FullyQualifiedName, row.DisplayName, shard));
            loads[shard] += Weight(group);
        }
        return new(SchemaVersion, revision, shardCount, assemblyPath, artifacts.OrderBy(pin => pin.Path, StringComparer.Ordinal).ToArray(),
            planned.OrderBy(row => row.FullyQualifiedName, StringComparer.Ordinal).ThenBy(row => row.Id).ToArray(), timings);
    }

    public static void Validate(TestPlan plan)
    {
        Require(plan.SchemaVersion == SchemaVersion, "Unsupported plan schema.");
        var expected = Create(plan.Revision, plan.AssemblyPath, plan.Artifacts,
            plan.Cases.Select(row => new DiscoveredCase(row.Id, row.FullyQualifiedName, row.DisplayName)), plan.ShardCount, plan.Timings);
        Require(expected.Cases.SequenceEqual(plan.Cases), "Plan ordering or method assignment is invalid.");
        Require(expected.Timings is null ? plan.Timings is null : expected.Timings.SequenceEqual(plan.Timings!), "Plan timing estimates are not canonical.");
    }

    public static TestPlan Read(string path, string expectedHash)
    {
        Require(Hex64().IsMatch(expectedHash), "A producer manifest SHA256 is required.");
        var bytes = File.ReadAllBytes(path);
        Require(string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.OrdinalIgnoreCase), "Manifest hash mismatch.");
        var plan = JsonSerializer.Deserialize<TestPlan>(bytes, Json) ?? throw new InvalidDataException("Empty manifest.");
        Validate(plan);
        return plan;
    }

    public static void VerifyArtifacts(TestPlan plan, string manifestPath)
    {
        foreach (var pin in plan.Artifacts)
            Require(string.Equals(HashFile(Resolve(manifestPath, pin.Path)), pin.Sha256, StringComparison.OrdinalIgnoreCase), "Compiled artifact hash mismatch.");
    }

    public static string Resolve(string manifestPath, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var root = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Require(resolved.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Artifact escapes manifest directory.");
        return resolved;
    }

    public static string Filter(TestPlan plan, int shard)
    {
        Require(shard >= 0 && shard < plan.ShardCount, "Invalid shard index.");
        var methods = plan.Cases.Where(row => row.Shard == shard).Select(row => row.FullyQualifiedName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Require(methods.Length > 0, "Shard has no tests.");
        return string.Join('|', methods.Select(method => $"FullyQualifiedName={method}"));
    }

    public static void WriteRunSettings(string path, TestPlan plan, int shard)
    {
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
        writer.WriteStartElement("RunSettings");
        writer.WriteStartElement("RunConfiguration");
        writer.WriteElementString("TestCaseFilter", Filter(plan, shard));
        writer.WriteElementString("TreatNoTestsAsError", "true");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void ValidateRelativePath(string path)
        => Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
            && path.Split('/').All(part => part.Length > 0 && part is not "." and not ".."), "Artifact path must stay relative to the manifest.");

    [GeneratedRegex("\\A[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hex40();
    [GeneratedRegex("\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hex64();
    [GeneratedRegex("\\A[A-Za-z_][A-Za-z0-9_+`]*(?:\\.[A-Za-z_][A-Za-z0-9_+`]*)+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex MethodName();
}
