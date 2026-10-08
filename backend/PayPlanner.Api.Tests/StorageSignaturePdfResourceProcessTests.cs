using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentSignaturePdfMeasuresSuccessfulProducerResources(bool postgres)
    {
        const string phase = "pdf-metrics";
        const int size = StorageStageFileReceipt.MaximumBlockBytes * 4 + 4;
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var source = RandomNumberGenerator.GetBytes(size);
        "%PDF-1.7"u8.CopyTo(source);
        var sourceSha = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "pdf-owned-source.pdf"), source);
        var assembly = Path.Combine(AppContext.BaseDirectory, "PayPlanner.StorageStageWriterProbe.dll");
        Assert.True(File.Exists(assembly));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = f.Root
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "WINDIR", "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["Email__Enabled"] = "false";
        start.Environment["BackgroundServices__Enabled"] = "false";
        if (postgres) start.Environment["PAYPLANNER_TEST_POSTGRES"] = f.AdminConnectionString!;
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(postgres ? "postgres" : "sqlite");
        start.ArgumentList.Add(f.Root);
        start.ArgumentList.Add(phase);
        using var writer = Process.Start(start) ?? throw new InvalidOperationException("Owned checkpoint writer did not start.");
        var processIdentity = OwnedProcessIdentity.Capture(writer);
        var output = writer.StandardOutput.ReadToEndAsync();
        var errors = writer.StandardError.ReadToEndAsync();
        try
        {
            var readyPath = Path.Combine(f.Root, "writer-ready.json");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(readyPath) && !writer.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(File.Exists(readyPath), writer.HasExited ? await errors : "Checkpoint writer readiness timed out.");
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));
            Assert.Equal(writer.Id, ready.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.Equal(processIdentity, ready.RootElement.GetProperty("ProcessIdentity").GetString());
            var ticket = ready.RootElement.GetProperty("Ticket").Deserialize<StorageStageCapacityTicket>()!;
            var metrics = ready.RootElement.GetProperty("Metrics");
            Assert.Equal(size, metrics.GetProperty("SourceBytes").GetInt64());
            Assert.Equal((size - 5 + 81919) / 81920 + 2, metrics.GetProperty("SourceReads").GetInt32());
            Assert.Equal(10, metrics.GetProperty("ReservationGroups").GetInt32());
            Assert.Equal(10, metrics.GetProperty("CopyGroups").GetInt32());
            // Five block checkpoints, size confirmation, settlement and domain registration.
            // Final release sees an already released reservation and adds no commit.
            Assert.Equal(8, metrics.GetProperty("Commits").GetInt32());
            Assert.Equal(1, metrics.GetProperty("SourceOpens").GetInt32());
            Assert.True(metrics.GetProperty("SourceClosed").GetBoolean());
            Assert.True(metrics.GetProperty("TotalSqlCommands").GetInt32() > 20);
            Assert.True(metrics.GetProperty("AllocatedBytes").GetInt64() >= 0);
            Assert.True(metrics.GetProperty("ManagedHeapBytes").GetInt64() > 0);
            Assert.True(metrics.GetProperty("WorkingSetBytes").GetInt64() > 0);
            Assert.True(metrics.GetProperty("PeakWorkingSetBytes").GetInt64() > 0);
            Assert.True(metrics.GetProperty("ElapsedMs").GetInt64() >= 0);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(metrics.GetProperty("ProcessReadOperations").GetUInt64() > 0);
                Assert.True(metrics.GetProperty("ProcessWriteOperations").GetUInt64() > 0);
                Assert.True(metrics.GetProperty("ProcessReadBytes").GetUInt64() > 0);
                Assert.True(metrics.GetProperty("ProcessWriteBytes").GetUInt64() >= size);
            }
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(ticket.RootIdentity, row.StageRootIdentity);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
            Assert.Equal(5, receipt!.Sequence);
            Assert.Equal(metrics.GetProperty("HandoffSize").GetInt64(), receipt.PlannedBytes);
            Assert.InRange(receipt.PlannedBytes, (long)size, ticket.SizeBytes);
            Assert.Equal(ticket.SizeBytes, receipt.UpperBound);
            Assert.Equal(receipt.PlannedBytes, row.Bytes);
            Assert.NotNull(row.ProductContextJson);
            var expectedSha = metrics.GetProperty("HandoffSha256").GetString();
            Assert.Equal(expectedSha, receipt.PlannedSha256);
            Assert.NotNull(expectedSha);
            Assert.Equal(64, expectedSha.Length);
            var snapshot = metrics.GetProperty("SnapshotPath").GetString()!;
            Assert.StartsWith(Path.GetFullPath(Path.Combine(f.Root, "objects")) + Path.DirectorySeparatorChar, Path.GetFullPath(snapshot));
            Assert.Equal(sourceSha, expectedSha);
            Assert.Equal(source, await File.ReadAllBytesAsync(snapshot));

            Assert.Equal(1, await f.Db.StorageCapacityReservations.CountAsync());
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
            Console.WriteLine($"Owned signature PDF resource metrics: database={(postgres ? "PostgreSQL" : "SQLite")}; metrics={metrics.GetRawText()}");
            await StopOwnedAsync();
            Assert.Equal(source, await File.ReadAllBytesAsync(snapshot));
            Assert.Equal(1, await f.Db.ElectronicSignatureEnvelopes.CountAsync());
            Assert.False(File.Exists(ticket.AbsolutePath));
        }
        finally { await StopOwnedAsync(); await output; await errors; }

        async Task StopOwnedAsync()
        {
            if (writer.HasExited) return;
            using var observed = Process.GetProcessById(writer.Id);
            Assert.Equal(processIdentity, OwnedProcessIdentity.Capture(observed));
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
