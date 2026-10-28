using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PayPlanner.TestShards;

internal static class BoundedTestRunner
{
    public static async Task<int> RunAsync(TestPlan plan, string manifest, string hash, int shard, string directory, string workers, string suite, string vstestConsolePath)
    {
        ShardPlan.Require(!Directory.Exists(directory) && !File.Exists(directory), "Preserve existing shard results.");
        var partitionCount = 1;
        ExecutionBatch[] batches = [];
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "batches"));
        var started = DateTime.UtcNow;
        var exit = -1;
        string? errorKind = null;
        var events = new List<object>();
        var gate = new object();
        void Record(object value)
        {
            lock (gate)
            {
                events.Add(value);
                var file = Path.Combine(directory, "execution.json");
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    ManifestSha256 = hash,
                    Shard = shard,
                    Suite = suite,
                    RequestedWorkers = workers,
                    BatchCount = partitionCount,
                    Events = events
                }, ShardPlan.Json));
                File.Move(temporary, file, overwrite: true);
            }
        }
        async Task ExecuteBatch(ExecutionBatch batch)
        {
            var root = Path.Combine(directory, "batches", batch.Id);
            Directory.CreateDirectory(root);
            var settings = Path.Combine(root, "batch.runsettings");
            ShardPlan.WriteRunSettings(settings, batch.Cases);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "exec", vstestConsolePath, ShardPlan.Resolve(manifest, plan.AssemblyPath), "/Settings:" + settings,
                         "/Logger:console;verbosity=normal", "/Logger:trx;LogFileName=results.trx", "/ResultsDirectory:" + root }) start.ArgumentList.Add(argument);
            ExecutionScheduling.ConfigureWorkerEnvironment(start.Environment, batch.Lane);
            var batchStarted = DateTime.UtcNow;
            var batchExit = -1;
            string? batchError = null;
            try
            {
                await using var stdout = File.Create(Path.Combine(root, "stdout.log"));
                await using var stderr = File.Create(Path.Combine(root, "stderr.log"));
                using var process = Process.Start(start) ?? throw new InvalidOperationException("The batch process did not start.");
                var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                var error = process.StandardError.BaseStream.CopyToAsync(stderr);
                try
                {
                    Record(new
                    {
                        Kind = "started",
                        batch.Id,
                        batch.Lane,
                        Utc = batchStarted,
                        ProcessId = process.Id,
                        Cases = batch.Cases.Length,
                        GcHeapHardLimit = start.Environment["DOTNET_GCHeapHardLimit"]
                    });
                    Console.WriteLine(JsonSerializer.Serialize(new { Batch = batch.Id, Status = "started", Cases = batch.Cases.Length }));
                }
                finally
                {
                    // Telemetry or console failures must not abandon an already running child.
                    await OwnedOutputDrain.WaitAsync(process.WaitForExitAsync(), output, error, () =>
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { /* Exit raced a failed output sink. */ }
                    });
                    batchExit = process.ExitCode;
                }
                long? peakWorkingSet = null;
                try { peakWorkingSet = process.PeakWorkingSet64; }
                catch (InvalidOperationException) { /* Some hosts discard this optional metric at process exit. */ }
                catch (System.ComponentModel.Win32Exception) { /* Native status remains authoritative. */ }
                Record(new
                {
                    Kind = "native-exit",
                    batch.Id,
                    batch.Lane,
                    Utc = DateTime.UtcNow,
                    ExitCode = batchExit,
                    VstestPeakWorkingSetBytes = peakWorkingSet
                });
            }
            catch (Exception error)
            {
                batchError = error.GetType().Name;
                throw;
            }
            finally
            {
                WriteNew(Path.Combine(root, "run.json"), new BatchRun(1, plan.Revision, hash, shard, batch.Id, batch.Lane,
                    plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256, batchStarted, DateTime.UtcNow, batchExit, batchError));
            }
            BatchResults.Verify(plan, hash, shard, batch, root);
            WriteNew(Path.Combine(root, "qualification.json"), new
            {
                Qualified = true,
                Batch = batch.Id,
                Cases = batch.Cases.Length,
                ManifestSha256 = hash,
                Revision = plan.Revision
            });
            Console.WriteLine(JsonSerializer.Serialize(new { Batch = batch.Id, Status = "passed", Cases = batch.Cases.Length }));
        }
        try
        {
            var admitted = HostResources.Read();
            var admittedWorkers = ExecutionScheduling.WorkerBudget(workers, admitted.AvailableBytes, admitted.Processors);
            partitionCount = ExecutionScheduling.PartitionCapacity(workers, admitted.Processors);
            batches = ExecutionScheduling.Batches(plan, shard, suite, partitionCount);
            Record(new
            {
                Kind = "initial-budget",
                Utc = DateTime.UtcNow,
                BatchCount = partitionCount,
                AdmittedWorkers = admittedWorkers,
                admitted.AvailableBytes,
                admitted.Processors
            });
            await BoundedBatchScheduler.RunAsync(batches, workers, partitionCount, HostResources.Read, ExecuteBatch,
                value => Record(new
                {
                    value.Kind,
                    value.Lane,
                    Utc = DateTime.UtcNow,
                    value.Workers,
                    value.AvailableBytes,
                    value.Processors,
                    value.Active
                }));
            ShardPlan.VerifyArtifacts(plan, manifest);
            BatchResults.Merge(plan, hash, shard, directory, suite, Path.Combine(directory, suite == "all" ? "results.trx" : "subset-results.trx"), partitionCount);
            exit = 0;
        }
        catch (Exception error)
        {
            errorKind = error.GetType().Name;
            throw;
        }
        finally
        {
            WriteNew(Path.Combine(directory, "run.json"), new ShardRun(ShardPlan.SchemaVersion, plan.Revision, hash, shard,
                plan.Artifacts.Single(pin => pin.Path == plan.AssemblyPath).Sha256, started, DateTime.UtcNow, exit, errorKind, suite, partitionCount));
        }
        if (suite == "all")
        {
            var qualification = TrxVerifier.Verify(plan, hash, shard, directory);
            WriteNew(Path.Combine(directory, "qualification.json"), qualification);
            Console.WriteLine(JsonSerializer.Serialize(qualification));
        }
        else
        {
            var qualification = BatchResults.VerifySubset(plan, hash, shard, directory, suite);
            WriteNew(Path.Combine(directory, "subset-qualification.json"), qualification);
            Console.WriteLine(JsonSerializer.Serialize(qualification));
        }
        return 0;
    }

    private static void WriteNew<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, ShardPlan.Json));
        File.Move(temporary, path, overwrite: false);
    }
}

internal static class HostResources
{
    public static ExecutionResources Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            ShardPlan.Require(GlobalMemoryStatusEx(ref memory), "Available host RAM could not be measured.");
            var processors = GetActiveProcessorCount(ushort.MaxValue);
            ShardPlan.Require(processors is > 0 and < int.MaxValue, "The actual host processor count could not be measured.");
            return new(memory.AvailablePhysical, (int)processors);
        }
        throw new InvalidDataException("Bounded execution is qualified only on Windows; other hosts require their own resource-limit verification.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
