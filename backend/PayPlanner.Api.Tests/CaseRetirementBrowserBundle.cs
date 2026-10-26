using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Class-owned build artifact and address only; never owns a reusable API or database.</summary>
public sealed class CaseRetirementBrowserBundle : IAsyncLifetime, IAsyncDisposable
{
    private const string Prefix = "pp_case_retirement_bundle_";
    private TcpListener reservation;
    private readonly string marker;
    private int active;
    private bool faulted;
    public string Root { get; }
    public string OwnerId { get; } = Guid.NewGuid().ToString("N");
    public Uri ApiAddress { get; }

    public CaseRetirementBrowserBundle()
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        RejectReparse(parent);
        Root = Path.Combine(parent, Prefix + OwnerId);
        if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Bundle owner already exists.");
        Directory.CreateDirectory(Root);
        reservation = Reserve(0);
        ApiAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
        marker = JsonSerializer.Serialize(new { schema = 1, id = OwnerId, root = Root, apiOrigin = ApiAddress.GetLeftPart(UriPartial.Authority) });
        try { File.WriteAllText(Path.Combine(Root, "owner.json"), marker); }
        catch { reservation.Stop(); throw; }
    }

    internal Lease Acquire()
    {
        if (faulted || Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("The class-owned API address is busy or unproven.");
        try
        {
            ValidateOwner();
            if (File.Exists(Path.Combine(Root, "failure.json")) || File.Exists(Path.Combine(Root, "bundle-use.lock"))
                || (File.Exists(Path.Combine(Root, "build-attempt.json")) && !File.Exists(Path.Combine(Root, "bundle-ready.json"))))
                throw new IOException("A failed or interrupted class build cannot admit another runtime.");
            return new Lease(this);
        }
        catch { Interlocked.Exchange(ref active, 0); throw; }
    }

    private void ValidateOwner()
    {
        if (Path.GetDirectoryName(Root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || Path.GetFileName(Root) != Prefix + OwnerId) throw new IOException("Invalid bundle root.");
        RejectReparse(Root);
        var path = Path.Combine(Root, "owner.json");
        RejectReparse(path);
        if (File.ReadAllText(path) != marker) throw new IOException("Bundle ownership changed.");
    }

    private static TcpListener Reserve(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { listener.Start(); return listener; }
        catch { listener.Stop(); throw; }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Bundle ownership cannot follow a reparse point.");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Do not erase a surviving child/ambiguous artifact after an interrupted scenario.
        reservation.Stop();
        if (Volatile.Read(ref active) != 0 || faulted) throw new IOException("Bundle address was not safely returned.");
        ValidateOwner();
        if (File.Exists(Path.Combine(Root, "bundle-use.lock"))) throw new IOException("A bundle child has not released its lease.");
        if (File.Exists(Path.Combine(Root, "failure.json"))
            || (File.Exists(Path.Combine(Root, "build-attempt.json")) && !File.Exists(Path.Combine(Root, "bundle-ready.json"))))
            throw new IOException("Failed build owner is preserved for inspection: " + Root);
        if (File.Exists(Path.Combine(Root, "class-inputs.json")))
        {
            // This full dependency/tool boundary is intentionally timed separately from cases.
            // Failure invalidates class qualification, even if individual browser outcomes passed.
            var repository = SyntheticWritableBackendProcess.FindRepositoryDirectory();
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = repository.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var inherited = start.Environment.ToArray();
            start.Environment.Clear();
            foreach (var (key, value) in inherited)
                if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE" }
                    .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
            start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", "case-retirement-production-bundle.mjs"));
            start.ArgumentList.Add("--finalize");
            start.ArgumentList.Add(Root);
            start.ArgumentList.Add(OwnerId);
            using var process = Process.Start(start) ?? throw new IOException("Bundle finalization could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await process.WaitForExitAsync(timeout.Token);
                Console.WriteLine(await stdout);
                if (process.ExitCode != 0) throw new IOException("Class bundle inputs changed or could not be verified. " + await stderr);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            CheckTree(Root);
            var artifactsParent = repository.FullName;
            foreach (var part in new[] { ".playwright-cli", "case-retirement-bundles" })
            {
                artifactsParent = Path.Combine(artifactsParent, part);
                if (Directory.Exists(artifactsParent)) RejectReparse(artifactsParent);
                else Directory.CreateDirectory(artifactsParent);
            }
            var artifacts = Path.Combine(artifactsParent, OwnerId);
            if (Directory.Exists(artifacts) || File.Exists(artifacts)) throw new IOException("Bundle evidence already exists.");
            Directory.CreateDirectory(artifacts);
            File.Copy(Path.Combine(Root, "owner.json"), Path.Combine(artifacts, "owner.json"));
            File.Copy(Path.Combine(Root, "build-attempt.json"), Path.Combine(artifacts, "build-attempt.json"));
            File.Copy(Path.Combine(Root, "bundle-ready.json"), Path.Combine(artifacts, "bundle-ready.json"));
            File.Copy(Path.Combine(Root, "class-inputs.json"), Path.Combine(artifacts, "class-inputs.json"));
            File.Copy(Path.Combine(Root, "bundle", "descriptor.json"), Path.Combine(artifacts, "descriptor.json"));
        }
        CheckTree(Root);
        Directory.Delete(Root, recursive: true);

        static void CheckTree(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(entry);
                if (Directory.Exists(entry)) CheckTree(entry);
            }
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    internal sealed class Lease(CaseRetirementBrowserBundle owner) : IDisposable
    {
        private bool handedToProcess;
        private bool disposed;
        internal Uri ApiAddress => owner.ApiAddress;
        internal string Root => owner.Root;
        internal string OwnerId => owner.OwnerId;
        internal void ReleaseReservationForStart()
        {
            if (disposed || handedToProcess) throw new InvalidOperationException("API address handoff is single-use.");
            owner.ValidateOwner();
            owner.reservation.Stop();
            handedToProcess = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Called only after the particular owned backend has exited. A surviving listener
            // or bind race is a failure, never permission to use it or choose another address.
            try
            {
                owner.ValidateOwner();
                if (handedToProcess) owner.reservation = Reserve(owner.ApiAddress.Port);
            }
            catch { owner.faulted = true; throw; }
            finally { Interlocked.Exchange(ref owner.active, 0); }
        }
    }
}
