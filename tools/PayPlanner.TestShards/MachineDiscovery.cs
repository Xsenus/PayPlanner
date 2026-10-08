using System.Diagnostics;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace PayPlanner.TestShards;

internal static class MachineDiscovery
{
    public static async Task<DiscoveredCase[]> ReadAsync(string sdk, string assembly)
    {
        var parameters = new ConsoleParameters { TraceLevel = TraceLevel.Off };
        parameters.EnvironmentVariables["Email__Enabled"] = "false";
        parameters.EnvironmentVariables["BackgroundServices__Enabled"] = "false";
        parameters.EnvironmentVariables["PAYPLANNER_TEST_POSTGRES"] = null;
        var wrapper = new VsTestConsoleWrapper(Path.Combine(sdk, "vstest.console.dll"), parameters);
        var collector = new Collector(assembly);
        Exception? primary = null;
        try
        {
            // The public synchronous bootstrap starts the runner before waiting for its handshake.
            var operation = Task.Run(async () =>
            {
                wrapper.InitializeExtensions(Array.Empty<string>());
                await wrapper.DiscoverTestsAsync(new[] { assembly }, "<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>", collector);
            });
            _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await operation.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (Exception error)
        {
            primary = error;
            try { wrapper.CancelDiscovery(); } catch { /* Preserve the discovery failure. */ }
            throw;
        }
        finally
        {
            try { wrapper.EndSession(); }
            catch when (primary is not null) { /* Cleanup must not hide the primary discovery failure. */ }
        }
        return collector.Complete();
    }

    private sealed class Collector(string assembly) : ITestDiscoveryEventsHandler
    {
        private readonly object gate = new();
        private readonly List<DiscoveredCase> cases = [];
        private long total;
        private int completions;
        private bool invalid;

        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
        {
            if (discoveredTestCases is null) return;
            lock (gate)
                foreach (var row in discoveredTestCases)
                {
                    invalid |= !string.Equals(Path.GetFullPath(row.Source), assembly, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    cases.Add(new(row.Id, row.FullyQualifiedName, row.DisplayName));
                }
        }

        public void HandleDiscoveryComplete(long totalTests, IEnumerable<TestCase>? lastChunk, bool isAborted)
        {
            lock (gate)
            {
                HandleDiscoveredTests(lastChunk);
                total = totalTests;
                completions++;
                invalid |= isAborted;
            }
        }

        public void HandleLogMessage(TestMessageLevel level, string? message)
        {
            if (level is TestMessageLevel.Error or TestMessageLevel.Warning) lock (gate) invalid = true;
        }

        public void HandleRawMessage(string rawMessage) { /* Do not retain raw adapter protocol or serialized test objects. */ }

        public DiscoveredCase[] Complete()
        {
            lock (gate)
            {
                ShardPlan.Require(!invalid && completions == 1 && total == cases.Count && total > 0, "Discovery was incomplete, aborted or reported a warning/error.");
                return cases.ToArray();
            }
        }
    }
}
