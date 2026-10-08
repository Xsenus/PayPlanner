using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentPartialCheckpointFaultRetainsBytesAndDebtAcrossKill(bool postgres, bool afterFirst)
    {
        var phase = afterFirst ? "checkpoint-partial-next" : "checkpoint-partial-first";
        const int blockSize = StorageStageFileReceipt.MaximumBlockBytes;
        var first = new byte[blockSize]; Array.Fill(first, (byte)0x31);
        var planned = afterFirst ? first.Concat(Enumerable.Repeat((byte)0x52, blockSize)).ToArray() : first;
        var physical = planned.AsSpan(0, (afterFirst ? blockSize : 0) + blockSize / 2).ToArray();
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
        var processIdentity = OwnedProcessIdentity.Capture(writer);
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
            Assert.Equal(processIdentity, ready.RootElement.GetProperty("ProcessIdentity").GetString());
            var ticket = ready.RootElement.GetProperty("Ticket").Deserialize<StorageStageCapacityTicket>()!;
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            var json = row.StageFileReceiptJson;
            Assert.True(StorageStageFileReceipt.TryParse(json, out var proof));
            Assert.Equal(ticket.ObjectId, proof!.ObjectId);
            Assert.Equal(ticket.BindingHash, proof.AdmissionBindingHash);
            Assert.Equal(ticket.RootIdentity, row.StageRootIdentity);
            Assert.Equal(ticket.WriterMarkerIdentity, row.StageWriterMarkerIdentity);
            Assert.Equal(planned.LongLength, proof.UpperBound);
            Assert.Equal(afterFirst ? 2 : 1, proof.Sequence);
            Assert.Equal(afterFirst ? blockSize : 0, proof.PreviousBytes);
            Assert.Equal(afterFirst ? Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant()
                : StorageStageFileReceipt.EmptySha256, proof.PreviousSha256);
            Assert.Equal(planned.LongLength, proof.PlannedBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(planned)).ToLowerInvariant(), proof.PlannedSha256);
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
            var preview = await recovery.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("incompleteFile", Assert.Single(preview.Items).Status);
            Assert.Equal(0, preview.Eligible);
            var applied = await recovery.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(0, applied.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                await f.StatusAsync(ticket.Key));
            Assert.Equal(physical, await File.ReadAllBytesAsync(ticket.AbsolutePath));
            Assert.Equal(planned.LongLength, (await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Bytes);
            Assert.Equal(64, new FileInfo(marker).Length);
            Assert.Equal(json, await f.Db.StorageCapacityReservations.AsNoTracking().Select(r => r.StageFileReceiptJson).SingleAsync());
            Assert.Equal(1, await f.Db.StorageCapacityReservations.CountAsync());
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        catch (SqliteException failure) when (!postgres)
        {
            // Keep the original failure; expose only bounded synthetic diagnostics.
            Console.WriteLine("PP_OWN_SQLITE_FAILURE " + JsonSerializer.Serialize(new
            {
                Phase = afterFirst ? "partial-after-first" : "partial-first",
                OwnedRoot = Path.GetFileName(f.Root),
                Code = failure.SqliteErrorCode,
                ExtendedCode = failure.SqliteExtendedErrorCode
            }));
            throw;
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
