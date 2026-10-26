[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedSid = 'S-1-5-21-944213604-1016845418-2036792573-1001'
$expectedRef = 'refs/heads/claude/practical-bardeen-pglcf3'
$projectRoot = 'D:\PayPlannerCIArtifacts'
$storeRoot = 'D:\PayPlannerCIArtifacts\store'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $actualSid = $identity.User.Value } finally { $identity.Dispose() }
if ([Environment]::MachineName.ToUpperInvariant() -cne 'XSENUS' -or $actualSid -cne $expectedSid -or
    $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_NAME -cne 'PayPlanner-XSENUS' -or
    $env:GITHUB_REPOSITORY -cne 'Xsenus/PayPlanner' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_REF -cne $expectedRef -or $env:GITHUB_JOB -cne 'frontend' -or $env:GITHUB_RUN_ATTEMPT -cne '1' -or
    $env:GITHUB_WORKFLOW_REF -cne ('Xsenus/PayPlanner/.github/workflows/deploy.yml@' + $expectedRef) -or
    $env:PAYPLANNER_CI_ARTIFACT_ROOT -cne $storeRoot) {
    throw 'Only the reviewed first-attempt XSENUS fixture-producer context may initialize these two new paths.'
}

# Directory.CreateDirectory(path, DirectorySecurity) can silently accept a racing
# existing directory. Use the same protected descriptor with exclusive native
# creation, which returns ERROR_ALREADY_EXISTS instead of accepting that race.
$nativeSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class PayPlannerStoreCreateNew03 {
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }
    [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES attributes);
    public static void Create(string path, byte[] descriptor) {
        IntPtr buffer = Marshal.AllocHGlobal(descriptor.Length);
        try {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
            SECURITY_ATTRIBUTES attributes = new SECURITY_ATTRIBUTES();
            attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
            attributes.lpSecurityDescriptor = buffer;
            attributes.bInheritHandle = 0;
            if (!CreateDirectoryW(path, ref attributes)) {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Exclusive CreateDirectoryW failed; no existing directory is accepted or modified.");
            }
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
'@
Add-Type -TypeDefinition $nativeSource -Language CSharp

function Sha256([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Require-Absent([string]$Path) {
    try { $null = [IO.File]::GetAttributes($Path) }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { return }
        throw
    }
    throw 'A proposed new directory already exists; stop without modifying it.'
}
function Read-OrdinaryAcl([string]$Path) {
    $attributes = [IO.File]::GetAttributes($Path)
    if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Expected an ordinary non-reparse directory.' }
    return ([IO.DirectoryInfo]::new($Path)).GetAccessControl([Security.AccessControl.AccessControlSections]::Access -bor [Security.AccessControl.AccessControlSections]::Owner -bor [Security.AccessControl.AccessControlSections]::Group)
}
function New-PrivateSecurity([string]$OwnerSid) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($OwnerSid))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $null = $acl.AddAccessRule($rule)
    }
    return $acl
}
function Verify-PrivateAcl([string]$Path, [string]$OwnerSid) {
    $acl = Read-OrdinaryAcl $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $OwnerSid -or -not $acl.AreAccessRulesProtected) { throw 'New directory owner/protection differs.' }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $allowed = @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')
    if ($rules.Count -ne 3) { throw 'Expected exactly three private ACL entries.' }
    foreach ($sid in $allowed) {
        $matches = @($rules | Where-Object { $_.IdentityReference.Value -ceq $sid })
        if ($matches.Count -ne 1) { throw 'Private ACL SID differs.' }
        $rule = $matches[0]
        if ($rule.IsInherited -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            [int64]$rule.FileSystemRights -ne 2032127 -or [int]$rule.InheritanceFlags -ne 3 -or [int]$rule.PropagationFlags -ne 0) { throw 'Private ACL rights or inheritance differ.' }
    }
    return [ordered]@{ path = $Path; ownerSid = $OwnerSid; protected = $true; fullControlSids = $allowed; descriptorSha256 = Sha256 $acl.GetSecurityDescriptorBinaryForm() }
}
function Write-NewJson([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 10) + [Environment]::NewLine)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

$report = [ordered]@{
    status = 'failed'; scope = 'Create exactly two previously absent private PayPlanner fixture-store paths'
    host = [Environment]::MachineName; sid = $actualSid; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT; headSha = $env:GITHUB_SHA
    createdPaths = @(); privateAcl = @(); freeBytesBefore = $null; freeBytesAfter = $null; volumeAclUnchanged = $false
    qualifiesCI = $false; qualifiesDeployment = $false; transportVerified = $false; priorSymlinkFailureUnchanged = 1314
    existingAclModified = $false; error = $null
}
$exitCode = 1
$resultPath = $null
try {
    if ($env:GITHUB_RUN_ID -notmatch '^[1-9][0-9]+$' -or $env:GITHUB_SHA -notmatch '^[a-f0-9]{40}$') { throw 'Invalid run identity.' }
    $null = Read-OrdinaryAcl $env:RUNNER_TEMP
    $resultPath = Join-Path $env:RUNNER_TEMP ('payplanner-store-init-' + [Guid]::NewGuid().ToString('N') + '.json')
    foreach ($workPath in @($env:GITHUB_WORKSPACE, $env:RUNNER_TEMP)) {
        $fullPath = [IO.Path]::GetFullPath($workPath).TrimEnd('\')
        if ($fullPath -ieq $projectRoot -or $fullPath.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $projectRoot.StartsWith($fullPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Project store must be outside checkout and runner temp.' }
    }
    $drive = [IO.DriveInfo]::new('D:\')
    $reserve = [int64](20GB + 128MB)
    if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed -or @('NTFS', 'ReFS') -notcontains $drive.DriveFormat -or $drive.AvailableFreeSpace -lt $reserve) { throw 'D must be a fixed security-capable volume with at least 20GiB plus 128MiB available.' }
    $report.freeBytesBefore = $drive.AvailableFreeSpace
    $volumeAcl = Read-OrdinaryAcl 'D:\'
    $volumeBeforeSha = Sha256 $volumeAcl.GetSecurityDescriptorBinaryForm()
    $allowed = @($expectedSid, 'S-1-5-18', 'S-1-5-32-544')
    if ($allowed -notcontains $volumeAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw 'Unexpected volume owner.' }
    foreach ($rule in $volumeAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        $inheritOnly = ([int]$rule.PropagationFlags -band 2) -ne 0
        # DELETE-self cannot replace a fixed volume; DELETE_CHILD/DACL/OWNER can compromise descendants.
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and -not $inheritOnly -and
            $allowed -notcontains $rule.IdentityReference.Value -and ([int64]$rule.FileSystemRights -band (64 -bor 262144 -bor 524288 -bor 268435456)) -ne 0) { throw 'Untrusted volume child-replacement or ownership permission.' }
    }
    Require-Absent $projectRoot
    Require-Absent $storeRoot
    $security = New-PrivateSecurity $expectedSid
    $descriptor = $security.GetSecurityDescriptorBinaryForm()
    [PayPlannerStoreCreateNew03]::Create($projectRoot, $descriptor)
    $report.createdPaths += $projectRoot
    $report.privateAcl += Verify-PrivateAcl $projectRoot $expectedSid
    Require-Absent $storeRoot
    [PayPlannerStoreCreateNew03]::Create($storeRoot, $descriptor)
    $report.createdPaths += $storeRoot
    $report.privateAcl += Verify-PrivateAcl $storeRoot $expectedSid
    $null = Verify-PrivateAcl $projectRoot $expectedSid
    $volumeAfterAcl = Read-OrdinaryAcl 'D:\'
    if ((Sha256 $volumeAfterAcl.GetSecurityDescriptorBinaryForm()) -cne $volumeBeforeSha) { throw 'Existing D-root ACL changed during initialization.' }
    $report.volumeAclUnchanged = $true
    $report.freeBytesAfter = ([IO.DriveInfo]::new('D:\')).AvailableFreeSpace
    if ($report.freeBytesAfter -lt $reserve) { throw 'Required disk reserve was not retained.' }
    $report.status = 'passed'
    $exitCode = 0
    Write-NewJson (Join-Path $projectRoot ('init-run-' + $env:GITHUB_RUN_ID + '-attempt-1.json')) $report
} catch {
    $cause = $_.Exception.GetBaseException()
    $nativeError = if ($cause -is [ComponentModel.Win32Exception]) { $cause.NativeErrorCode } else { $null }
    $report.status = 'failed'; $exitCode = 1
    $report.error = [ordered]@{ type = $cause.GetType().FullName; message = $cause.Message; nativeError = $nativeError }
}
if ($null -ne $resultPath) { Write-NewJson $resultPath $report }
[Console]::Out.WriteLine(($report | ConvertTo-Json -Depth 10))
exit $exitCode
