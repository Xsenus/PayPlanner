[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Diagnostic only: no AdjustTokenPrivileges, account rights, policy, ACL,
# registry, service or Developer Mode changes. Only fresh report/probe paths.
$nativeSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

public static class PayPlannerSymlinkDiagnostic02 {
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string system, string name, out Luid luid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string file, StringBuilder volume, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder name, uint nameSize,
        out uint serial, out uint maximumComponent, out uint flags, StringBuilder filesystem, uint filesystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string path);

    private static IntPtr TokenInformation(IntPtr token, int kind) {
        int needed;
        bool sizeSucceeded = GetTokenInformation(token, kind, IntPtr.Zero, 0, out needed);
        int error = Marshal.GetLastWin32Error();
        // Windows 10 TokenElevation returns ERROR_BAD_LENGTH with required=4
        // for this size-only call; the allocated read below must still succeed.
        bool acceptedSizeError = error == 122 || (kind == 20 && error == 24 && needed == 4);
        if (sizeSucceeded || needed <= 0 || !acceptedSizeError)
            throw new Win32Exception(error, "Cannot size token information: kind=" + kind + "; succeeded=" + sizeSucceeded + "; needed=" + needed + "; nativeError=" + error);
        IntPtr buffer = Marshal.AllocHGlobal(needed);
        if (!GetTokenInformation(token, kind, buffer, needed, out needed)) {
            error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(error, "Cannot read token information: kind=" + kind + "; needed=" + needed + "; nativeError=" + error);
        }
        return buffer;
    }

    private static int TokenInteger(IntPtr token, int kind) {
        IntPtr buffer = TokenInformation(token, kind);
        try { return Marshal.ReadInt32(buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static Dictionary<string, object> ReadProcessToken() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query current process token");
        try {
            var result = new Dictionary<string, object>();
            IntPtr user = TokenInformation(token, 1);
            try {
                var entry = (SidAndAttributes)Marshal.PtrToStructure(user, typeof(SidAndAttributes));
                var sid = new SecurityIdentifier(entry.Sid);
                result["sid"] = sid.Value;
                try { result["username"] = sid.Translate(typeof(NTAccount)).Value; }
                catch (IdentityNotMappedException) { result["username"] = null; }
            } finally { Marshal.FreeHGlobal(user); }
            int type = TokenInteger(token, 8);
            int elevationType = TokenInteger(token, 18);
            result["tokenType"] = type == 1 ? "Primary" : type == 2 ? "Impersonation" : "Unknown:" + type;
            result["elevated"] = TokenInteger(token, 20) != 0;
            result["elevationType"] = elevationType == 1 ? "Default" : elevationType == 2 ? "Full" : elevationType == 3 ? "Limited" : "Unknown:" + elevationType;
            Luid wanted;
            if (!LookupPrivilegeValueW(null, "SeCreateSymbolicLinkPrivilege", out wanted))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve symbolic-link privilege identifier");
            bool present = false;
            uint attributes = 0;
            IntPtr privileges = TokenInformation(token, 3);
            try {
                int count = Marshal.ReadInt32(privileges);
                if (count < 0 || count > 1024) throw new InvalidOperationException("Unexpected token privilege count");
                for (int i = 0; i < count; i++) {
                    int offset = 4 + (i * 12);
                    uint low = unchecked((uint)Marshal.ReadInt32(privileges, offset));
                    int high = Marshal.ReadInt32(privileges, offset + 4);
                    if (low == wanted.Low && high == wanted.High) {
                        present = true;
                        attributes = unchecked((uint)Marshal.ReadInt32(privileges, offset + 8));
                        break;
                    }
                }
            } finally { Marshal.FreeHGlobal(privileges); }
            result["symbolicLinkPrivilege"] = new Dictionary<string, object> {
                { "name", "SeCreateSymbolicLinkPrivilege" }, { "present", present },
                { "enabled", present && (attributes & 2) != 0 },
                { "enabledByDefault", present && (attributes & 1) != 0 }
            };
            return result;
        } finally { CloseHandle(token); }
    }

    public static Dictionary<string, object> ReadVolume(string path) {
        var volume = new StringBuilder(32768);
        if (!GetVolumePathNameW(path, volume, (uint)volume.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve TEMP volume");
        var label = new StringBuilder(261);
        var filesystem = new StringBuilder(261);
        uint serial, maximumComponent, flags;
        if (!GetVolumeInformationW(volume.ToString(), label, (uint)label.Capacity,
            out serial, out maximumComponent, out flags, filesystem, (uint)filesystem.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query TEMP filesystem");
        return new Dictionary<string, object> {
            { "volumeRoot", volume.ToString() }, { "filesystem", filesystem.ToString() },
            { "supportsReparsePoints", (flags & 0x00000080) != 0 },
            { "readOnlyVolume", (flags & 0x00080000) != 0 }
        };
    }

    public static void CreateNewDirectory(string path) {
        if (!CreateDirectoryW(path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create exclusive owned directory");
    }

    public static int TryCreateFileSymlink(string link, string target, uint flags) {
        if (CreateSymbolicLinkW(link, target, flags)) return 0;
        return Marshal.GetLastWin32Error();
    }

    public static void DeleteOwnedFile(string path) {
        if (DeleteFileW(path)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != 2 && error != 3) throw new Win32Exception(error, "Cannot remove owned probe file");
    }

    public static void RemoveOwnedDirectory(string path) {
        if (!RemoveDirectoryW(path))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove owned probe directory");
    }
}
'@

$report = [ordered]@{
    schema = 1
    scope = 'current-windows-process-token-and-owned-file-symlink-probe'
    status = 'failed'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    host = [Environment]::MachineName
    processId = $PID
    operatingSystem = [Environment]::OSVersion.VersionString
    process64Bit = [Environment]::Is64BitProcess
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    processToken = $null
    temp = $null
    probe = [ordered]@{ directory = $null; api = 'CreateSymbolicLinkW'; attempts = @(); created = $false; reparsePoint = $false; targetBytesMatched = $false; cleanupSucceeded = $false }
    errors = @()
    intendedExitCode = 1
    limitation = 'Describes this process token only; an interactive admin token does not establish the CI runner token. This is not a Vitest or full-CI qualification.'
}
$ownedReport = $null
$ownedProbe = $null
$linkPath = $null
$targetPath = $null
$exitCode = 1

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
    Add-Type -TypeDefinition $nativeSource -Language CSharp
    if ($OutputDirectory -notmatch '^[A-Za-z]:[\\/]') { throw 'OutputDirectory must be an absolute local drive path.' }
    $reportPath = [IO.Path]::GetFullPath($OutputDirectory)
    $parentPath = [IO.Path]::GetDirectoryName($reportPath)
    if (-not [IO.Directory]::Exists($parentPath)) { throw 'The output parent must already exist.' }
    if (([IO.File]::GetAttributes($parentPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The output parent must be an ordinary directory.' }
    [PayPlannerSymlinkDiagnostic02]::CreateNewDirectory($reportPath)
    $ownedReport = $reportPath
    $report.processToken = [PayPlannerSymlinkDiagnostic02]::ReadProcessToken()

    $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $tempAttributes = [IO.File]::GetAttributes($tempPath)
    $report.temp = [ordered]@{
        path = $tempPath
        isDirectory = (($tempAttributes -band [IO.FileAttributes]::Directory) -ne 0)
        isReparsePoint = (($tempAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        volume = [PayPlannerSymlinkDiagnostic02]::ReadVolume($tempPath)
    }
    if (-not $report.temp.isDirectory -or $report.temp.isReparsePoint) { throw 'TEMP must be an existing ordinary directory for this probe.' }
    $probePath = [IO.Path]::Combine($tempPath, 'pp-symlink-probe-' + [Guid]::NewGuid().ToString('N'))
    [PayPlannerSymlinkDiagnostic02]::CreateNewDirectory($probePath)
    $ownedProbe = $probePath
    $report.probe.directory = $probePath
    $targetPath = [IO.Path]::Combine($probePath, 'target.txt')
    $linkPath = [IO.Path]::Combine($probePath, 'link.txt')
    $payload = [Text.Encoding]::UTF8.GetBytes('PayPlanner owned symbolic-link diagnostic; no application data.')
    $stream = [IO.File]::Open($targetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($payload, 0, $payload.Length) } finally { $stream.Dispose() }

    # Same flag selection as Node/libuv: request unprivileged creation if the OS
    # already permits it; retry without that flag only for unsupported flag (87).
    # This does not enable Developer Mode or change/enable token privileges.
    $createError = [PayPlannerSymlinkDiagnostic02]::TryCreateFileSymlink($linkPath, 'target.txt', 2)
    $report.probe.attempts += [ordered]@{ flags = 2; succeeded = ($createError -eq 0); win32Error = $createError }
    if ($createError -eq 87) {
        $createError = [PayPlannerSymlinkDiagnostic02]::TryCreateFileSymlink($linkPath, 'target.txt', 0)
        $report.probe.attempts += [ordered]@{ flags = 0; succeeded = ($createError -eq 0); win32Error = $createError }
    }
    if ($createError -ne 0) { throw [ComponentModel.Win32Exception]::new($createError, 'Owned file-symlink creation failed') }
    $report.probe.created = $true
    $linkAttributes = [IO.File]::GetAttributes($linkPath)
    $report.probe.reparsePoint = (($linkAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    if (-not $report.probe.reparsePoint -or (($linkAttributes -band [IO.FileAttributes]::Directory) -ne 0)) { throw 'The created link is not a file reparse point.' }
    $actual = [IO.File]::ReadAllBytes($linkPath)
    $report.probe.targetBytesMatched = ([Convert]::ToBase64String($actual) -ceq [Convert]::ToBase64String($payload))
    if (-not $report.probe.targetBytesMatched) { throw 'Reading through the owned file symlink returned different bytes.' }
    $exitCode = 0
} catch {
    $report.errors += [ordered]@{ stage = 'diagnostic'; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
    $exitCode = 1
} finally {
    if ($null -ne $ownedProbe) {
        try {
            if ($null -ne $linkPath) { [PayPlannerSymlinkDiagnostic02]::DeleteOwnedFile($linkPath) }
            if ($null -ne $targetPath) { [PayPlannerSymlinkDiagnostic02]::DeleteOwnedFile($targetPath) }
            [PayPlannerSymlinkDiagnostic02]::RemoveOwnedDirectory($ownedProbe)
            $report.probe.cleanupSucceeded = $true
        } catch {
            $report.errors += [ordered]@{ stage = 'cleanup'; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
            $exitCode = 1
        }
    }
}

$report.finishedUtc = [DateTime]::UtcNow.ToString('o')
$report.intendedExitCode = $exitCode
if ($exitCode -eq 0) { $report.status = 'passed' }
$json = $report | ConvertTo-Json -Depth 9
if ($null -ne $ownedReport) {
    try {
        $resultPath = [IO.Path]::Combine($ownedReport, 'result.json')
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
        $stream = [IO.File]::Open($resultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    } catch {
        [Console]::Error.WriteLine('Cannot preserve diagnostic result: ' + $_.Exception.Message)
        exit 1
    }
}
[Console]::Out.WriteLine($json)
exit $exitCode
