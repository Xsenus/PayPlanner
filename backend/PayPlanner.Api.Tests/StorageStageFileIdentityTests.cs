using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageFileIdentityTests
{
    [Fact]
    public void DirectoryDescriptorIsNotAnOrdinaryStageFile()
    {
        using var root = new OwnedRoot();
        using var handle = OperatingSystem.IsWindows()
            ? OpenWindowsDirectory(root.Path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)
            : new SafeFileHandle(new IntPtr(OpenLinux(root.Path, 0)), ownsHandle: true);
        Assert.False(handle.IsInvalid);
        Assert.Null(StorageStageFileIdentity.Observe(handle));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task HardLinkedFileStaysUnknownAndBothNamesArePreserved()
    {
        using var root = new OwnedRoot();
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(root.File, bytes);
        var alias = System.IO.Path.Combine(root.Path, "alias.tmp");
        Assert.True(OperatingSystem.IsWindows() ? CreateWindowsHardLink(alias, root.File, IntPtr.Zero)
            : CreateLinuxHardLink(root.File, alias) == 0);
        using var file = new FileStream(root.File, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Null(StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.File));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(alias));
    }

    [Fact]
    public void InvalidAndClosedDescriptorsStayUnknown()
    {
        Assert.Null(StorageStageFileIdentity.Observe(null));
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        Assert.Null(StorageStageFileIdentity.Observe(invalid));
        using var root = new OwnedRoot();
        var stream = new FileStream(root.File, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var handle = stream.SafeFileHandle;
        stream.Dispose();
        Assert.Null(StorageStageFileIdentity.Observe(handle));
        Assert.Equal(0, new FileInfo(root.File).Length);
    }

    [Fact]
    public async Task ActualIdentityIsStableAcrossIndependentProcessAndDoesNotChangeBytesOrPosition()
    {
        using var root = new OwnedRoot();
        var bytes = new byte[] { 1, 2, 3, 4 };
        string identity;
        await using (var file = new FileStream(root.File, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await file.WriteAsync(bytes);
            file.Flush(flushToDisk: true);
            file.Position = 2;
            identity = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
            Assert.Equal(2, file.Position);
            Assert.Equal(4, file.Length);
            Assert.Equal(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        }
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root.Path
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "WINDIR", "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["Email__Enabled"] = "false";
        start.Environment["BackgroundServices__Enabled"] = "false";
        start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "PayPlanner.StorageStageWriterProbe.dll"));
        start.ArgumentList.Add("identity");
        start.ArgumentList.Add(root.Path);
        using var process = Process.Start(start) ?? throw new IOException("Owned identity process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(identity, JsonSerializer.Deserialize<string>(await output));
            Assert.Equal("", await errors);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await output;
            await errors;
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.File));
        Assert.Matches(OperatingSystem.IsWindows()
            ? "^windows-stage-file-v1:[0-9a-f]{16}:[0-9a-f]{32}:[0-9a-f]{16}$"
            : "^linux-stage-file-v1:[0-9a-f]{8}:[0-9a-f]{8}:[0-9a-f]{16}:[0-9a-f]{16}:[0-9a-f]{8}$", identity);
        Assert.Equal(identity, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(identity)));
    }

    [Fact]
    public async Task EqualBytesReplacementChangesIdentityButSameFileOverwriteDoesNotProveBytes()
    {
        using var root = new OwnedRoot();
        var bytes = new byte[] { 1, 2, 3, 4 };
        string identity;
        await using (var file = new FileStream(root.File, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await file.WriteAsync(bytes);
            file.Flush(flushToDisk: true);
            identity = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
            file.Position = 0;
            await file.WriteAsync(new byte[] { 9, 8, 7, 6 });
            file.Flush(flushToDisk: true);
            Assert.Equal(identity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        }
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(root.File))));
        var displaced = System.IO.Path.Combine(root.Path, "displaced.tmp");
        File.Move(root.File, displaced);
        await using (var replacement = new FileStream(root.File, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await replacement.WriteAsync(bytes);
            replacement.Flush(flushToDisk: true);
            Assert.NotEqual(identity, Assert.IsType<string>(StorageStageFileIdentity.Observe(replacement.SafeFileHandle)));
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.File));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await File.ReadAllBytesAsync(displaced));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle OpenWindowsDirectory(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWindowsHardLink(string alias, string original, IntPtr security);
    [DllImport("libc", EntryPoint = "open", ExactSpelling = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "link", ExactSpelling = true)]
    private static extern int CreateLinuxHardLink([MarshalAs(UnmanagedType.LPUTF8Str)] string original,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alias);

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pp_stage_fence_" + Guid.NewGuid().ToString("N"));
        public string File => System.IO.Path.Combine(Path, "identity.tmp");
        public OwnedRoot() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
