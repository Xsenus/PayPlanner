using System.Diagnostics;
using System.Globalization;
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
    [InlineData(false, "checkpoint-empty")]
    [InlineData(true, "checkpoint-empty")]
    [InlineData(false, "checkpoint-planned")]
    [InlineData(true, "checkpoint-planned")]
    [InlineData(false, "checkpoint-complete")]
    [InlineData(true, "checkpoint-complete")]
    public async Task IndependentCheckpointWriterRetainsExactBoundaryAcrossKill(bool postgres, string phase)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
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
        var started = writer.StartTime.ToUniversalTime();
        var output = writer.StandardOutput.ReadToEndAsync();
        var errors = writer.StandardError.ReadToEndAsync();
        try
        {
            var readyPath = Path.Combine(f.Root, "writer-ready.json");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(readyPath) && !writer.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(File.Exists(readyPath), writer.HasExited ? await errors : "Checkpoint writer readiness timed out.");
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));
            Assert.Equal(writer.Id, ready.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.Equal(started, DateTime.Parse(ready.RootElement.GetProperty("StartedUtc").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            var ticket = ready.RootElement.GetProperty("Ticket").Deserialize<StorageStageCapacityTicket>()!;
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            var json = row.StageFileReceiptJson;
            Assert.True(StorageStageFileReceipt.TryParse(json, out var proof));
            Assert.Equal(ticket.ObjectId, proof!.ObjectId);
            Assert.Equal(ticket.BindingHash, proof.AdmissionBindingHash);
            Assert.Equal(4, proof.UpperBound);
            Assert.Equal(phase == "checkpoint-empty" ? 0 : 1, proof.Sequence);
            Assert.Equal(0, proof.PreviousBytes);
            Assert.Equal(StorageStageFileReceipt.EmptySha256, proof.PreviousSha256);
            Assert.Equal(phase == "checkpoint-empty" ? 0 : 4, proof.PlannedBytes);
            Assert.Equal(phase == "checkpoint-empty" ? StorageStageFileReceipt.EmptySha256
                : Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant(), proof.PlannedSha256);
            var old = DateTime.UtcNow.AddMinutes(-30);
            await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, old));
            await using var fresh = f.NewContext();
            var recovery = f.CreateStages(fresh);
            foreach (var apply in new[] { false, true })
            {
                var live = await recovery.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
                Assert.Equal("writerNotProvenStopped", Assert.Single(live.Items).Status);
                Assert.Equal(0, live.Settled);
                Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
            }
            await StopOwnedAsync();
            var marker = Path.Combine(f.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
            var quiescentDeadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { await using var exclusive = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException) when (DateTime.UtcNow < quiescentDeadline) { await Task.Delay(20); }
            }
            await using (var file = new FileStream(ticket.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Equal(proof.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
            var complete = phase == "checkpoint-complete";
            var preview = await recovery.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(complete ? "eligible" : "incompleteFile", Assert.Single(preview.Items).Status);
            Assert.Equal(complete ? 1 : 0, preview.Eligible);
            var applied = await recovery.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(complete ? 1 : 0, applied.Settled);
            Assert.Equal(complete ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending,
                await f.StatusAsync(ticket.Key));
            Assert.Equal(complete ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>(), await File.ReadAllBytesAsync(ticket.AbsolutePath));
            Assert.Equal(json, await f.Db.StorageCapacityReservations.AsNoTracking().Select(r => r.StageFileReceiptJson).SingleAsync());
            Assert.Equal(1, await f.Db.StorageCapacityReservations.CountAsync());
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { await StopOwnedAsync(); await output; await errors; }

        async Task StopOwnedAsync()
        {
            if (writer.HasExited) return;
            using var observed = Process.GetProcessById(writer.Id);
            Assert.Equal(started, observed.StartTime.ToUniversalTime());
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
