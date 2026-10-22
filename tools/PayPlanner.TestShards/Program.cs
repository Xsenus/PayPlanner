using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PayPlanner.TestShards;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            ShardPlan.Require(args.Length > 0, "Use discover, run, verify, verify-restored or timings.");
            var options = Options(args.Skip(1).ToArray());
            Environment.SetEnvironmentVariable("Email__Enabled", "false");
            Environment.SetEnvironmentVariable("BackgroundServices__Enabled", "false");
            return args[0] switch
            {
                "discover" => await DiscoverAsync(options),
                "run" => await RunAsync(options),
                "verify" => Verify(options),
                "verify-restored" => VerifyRestored(options),
                "timings" => Timings(options),
                _ => throw new InvalidDataException("Unknown command."),
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Qualified = false,
                ErrorKind = error.GetType().Name,
                Reason = error is InvalidDataException ? error.Message : "The test shard gate did not complete.",
            }));
            return 1;
        }
    }

    private static async Task<int> DiscoverAsync(Dictionary<string, string> options)
    {
        var cachedTimings = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_TIMINGS");
        var timings = options.Remove("timings", out var timingPath) ? TestTimings.Read(timingPath).Methods
            : !string.IsNullOrWhiteSpace(cachedTimings) && File.Exists(cachedTimings) ? TestTimings.Read(cachedTimings).Methods : null;
        ExactOptions(options, "assembly", "manifest", "revision", "shards");
        var manifest = Path.GetFullPath(options["manifest"]);
        ShardPlan.Require(!File.Exists(manifest), "Preserve the existing manifest.");
        var assembly = Path.GetFullPath(options["assembly"]);
        var root = Path.GetDirectoryName(manifest)!;
        var relative = Path.GetRelativePath(root, assembly).Replace('\\', '/');
        var runnerConfig = Path.Combine(Path.GetDirectoryName(assembly)!, "xunit.runner.json");
        var pins = new[] { assembly, Path.ChangeExtension(assembly, ".deps.json"), Path.ChangeExtension(assembly, ".runtimeconfig.json"), Path.Combine(Path.GetDirectoryName(assembly)!, "PayPlanner.Api.dll") }
            .Concat(File.Exists(runnerConfig) ? [runnerConfig] : [])
            .Select(path => new ArtifactPin(Path.GetRelativePath(root, path).Replace('\\', '/'), ShardPlan.HashFile(path))).ToArray();
        var theories = AssemblyCatalog.ReadTheoryMethods(assembly);
        var sdk = await LocateSdkAsync();
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var path = Path.Combine(sdk, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var cases = await ReadCasesAsync(sdk, assembly);
        AssemblyCatalog.RequireEnumeratedTheories(cases, theories);
        var plan = ShardPlan.Create(options["revision"], relative, pins, cases, ParseInt(options["shards"]), timings);
        ShardPlan.VerifyArtifacts(plan, manifest);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(plan, ShardPlan.Json));
        var summary = new
        {
            Qualified = true,
            ManifestSha256 = ShardPlan.HashFile(manifest),
            plan.Revision,
            CaseCount = plan.Cases.Length,
            MethodCount = plan.Cases.Select(row => row.FullyQualifiedName).Distinct(StringComparer.Ordinal).Count(),
            TimedMethods = plan.Timings?.Length ?? 0,
            ShardCounts = Enumerable.Range(0, plan.ShardCount).Select(index => plan.Cases.Count(row => row.Shard == index)).ToArray(),
        };
        WriteJson(Path.ChangeExtension(manifest, ".summary.json"), summary);
        Console.WriteLine(JsonSerializer.Serialize(summary, ShardPlan.Json));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<DiscoveredCase[]> ReadCasesAsync(string sdk, string assembly) => MachineDiscovery.ReadAsync(sdk, assembly);

    private static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        ExactOptions(options, "manifest", "manifest-sha", "shard", "results");
        var manifest = Path.GetFullPath(options["manifest"]);
        var hash = options["manifest-sha"];
        var plan = ShardPlan.Read(manifest, hash);
        ShardPlan.VerifyArtifacts(plan, manifest);
        var shard = ParseInt(options["shard"]);
        _ = ShardPlan.Filter(plan, shard);
        var results = Path.GetFullPath(options["results"]);
        ShardPlan.Require(!Directory.Exists(results) && !File.Exists(results), "Preserve existing shard results.");
        Directory.CreateDirectory(results);
        var settings = Path.Combine(results, "shard.runsettings");
        ShardPlan.WriteRunSettings(settings, plan, shard);
        var assembly = ShardPlan.Resolve(manifest, plan.AssemblyPath);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[] { "vstest", assembly, "/Settings:" + settings, "/Logger:console;verbosity=normal", "/Logger:trx;LogFileName=results.trx", "/ResultsDirectory:" + results })
            start.ArgumentList.Add(argument);
        start.Environment["Email__Enabled"] = "false";
        start.Environment["BackgroundServices__Enabled"] = "false";
        var started = DateTime.UtcNow;
        var exit = -1;
        string? errorKind = null;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("VSTest did not start.");
            await process.WaitForExitAsync();
            exit = process.ExitCode;
        }
        catch (Exception error)
        {
            errorKind = error.GetType().Name;
            throw;
        }
        finally
        {
            WriteJson(Path.Combine(results, "run.json"), new ShardRun(ShardPlan.SchemaVersion, plan.Revision, hash, shard,
                plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256, started, DateTime.UtcNow, exit, errorKind));
        }
        ShardPlan.VerifyArtifacts(plan, manifest);
        var qualification = TrxVerifier.Verify(plan, hash, shard, results);
        WriteJson(Path.Combine(results, "qualification.json"), qualification);
        Console.WriteLine(JsonSerializer.Serialize(qualification));
        return 0;
    }

    private static int Verify(Dictionary<string, string> options)
    {
        ExactOptions(options, "manifest", "manifest-sha", "results-root");
        var manifest = Path.GetFullPath(options["manifest"]);
        var plan = ShardPlan.Read(manifest, options["manifest-sha"]);
        ShardPlan.VerifyArtifacts(plan, manifest);
        var root = Path.GetFullPath(options["results-root"]);
        var shards = TrxVerifier.VerifyAll(plan, options["manifest-sha"], root);
        var result = new { Qualified = true, plan.Revision, ManifestSha256 = options["manifest-sha"], CaseCount = shards.Sum(row => row.Cases), Shards = shards };
        WriteJson(Path.Combine(root, "aggregate-qualification.json"), result);
        Console.WriteLine(JsonSerializer.Serialize(result, ShardPlan.Json));
        return 0;
    }

    private static int VerifyRestored(Dictionary<string, string> options)
    {
        ExactOptions(options, "manifest", "manifest-sha", "results", "exit-code");
        var manifest = Path.GetFullPath(options["manifest"]);
        var hash = options["manifest-sha"];
        var plan = ShardPlan.Read(manifest, hash);
        ShardPlan.VerifyArtifacts(plan, manifest);
        var results = Path.GetFullPath(options["results"]);
        var qualification = TrxVerifier.VerifyRestored(plan, hash, results, ParseInt(options["exit-code"]));
        WriteJson(Path.Combine(results, "restored-qualification.json"), qualification);
        Console.WriteLine(JsonSerializer.Serialize(qualification));
        return 0;
    }

    private static int Timings(Dictionary<string, string> options)
    {
        ExactOptions(options, "manifest", "manifest-sha", "results-root", "output");
        var manifest = Path.GetFullPath(options["manifest"]);
        var plan = ShardPlan.Read(manifest, options["manifest-sha"]);
        ShardPlan.VerifyArtifacts(plan, manifest);
        var profile = TestTimings.FromQualifiedResults(plan, options["manifest-sha"], options["results-root"]);
        WriteJson(options["output"], profile);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            profile.Revision,
            Methods = profile.Methods.Length,
            Cases = profile.Methods.Sum(row => row.Cases),
            Slowest = profile.Methods.OrderByDescending(row => (decimal)row.AverageTicks * row.Cases).Take(20)
                .Select(row => new { row.FullyQualifiedName, row.Cases, Seconds = (decimal)row.AverageTicks * row.Cases / TimeSpan.TicksPerSecond }),
        }, ShardPlan.Json));
        return 0;
    }

    private static async Task<string> LocateSdkAsync()
    {
        var version = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "TestShardsSdkVersion").Value!;
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Cannot locate the build SDK.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var lines = (await stdout).Split('\n');
        _ = await stderr;
        ShardPlan.Require(process.ExitCode == 0, "Cannot list installed SDKs.");
        var paths = lines.Select(line => Regex.Match(line.Trim(), "^" + Regex.Escape(version) + " \\[(.+)\\]$", RegexOptions.CultureInvariant))
            .Where(match => match.Success).Select(match => Path.Combine(match.Groups[1].Value, version)).ToArray();
        ShardPlan.Require(paths.Length == 1 && File.Exists(Path.Combine(paths[0], "vstest.console.dll")), "Install the same SDK version that built the shard tool.");
        return paths[0];
    }

    private static Dictionary<string, string> Options(string[] arguments)
    {
        ShardPlan.Require(arguments.Length % 2 == 0, "Arguments must be --name value pairs.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
            ShardPlan.Require(arguments[index].StartsWith("--", StringComparison.Ordinal)
                && arguments[index].Length > 2 && options.TryAdd(arguments[index][2..], arguments[index + 1]), "Invalid or duplicate option.");
        return options;
    }

    private static void ExactOptions(Dictionary<string, string> options, params string[] expected)
        => ShardPlan.Require(options.Count == expected.Length && expected.All(options.ContainsKey), "Missing or unsupported command options.");

    private static int ParseInt(string text)
    {
        ShardPlan.Require(int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value), "Invalid integer option.");
        return value;
    }

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, ShardPlan.Json));
        File.Move(temporary, path, overwrite: false);
    }
}
