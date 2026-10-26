[CmdletBinding()]
param([switch]$Execute, [string]$Plan, [string]$Authorisation)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Execute) {
    [Console]::Out.WriteLine('{"status":"source-only-no-execution","storeCreated":false}')
    exit 0
}
# git -C does not override inherited Git repository/index/config selectors.
# Reject every inherited GIT_* name; never rewrite or forge an operator context.
$gitOverrides = @([Environment]::GetEnvironmentVariables().Keys | Where-Object {
    ([string]$_).StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)
})
if ($gitOverrides.Count -ne 0) { throw 'Inherited GIT_* overrides are forbidden; no native Git or store write was started.' }
$expectedSid = 'S-1-5-21-944213604-1016845418-2036792573-1001'
$projectRoot = 'D:\PayPlannerCIArtifacts'
$storeRoot = 'D:\PayPlannerCIArtifacts\main-store'
$reserve = [int64](20GB + 128MB)
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem -or
    -not [Environment]::Is64BitProcess -or [IntPtr]::Size -ne 8) { throw 'Use the reviewed 64-bit Windows local operator process.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $actualSid = $identity.User.Value } finally { $identity.Dispose() }
if ([Environment]::MachineName.ToUpperInvariant() -cne 'XSENUS' -or $actualSid -cne $expectedSid) {
    throw 'Only the reviewed local XSENUS operator SID may initialize the production store.'
}

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

function Read-OrdinaryBytes([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cnotmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\')) { throw 'Ordinary local absolute control path required.' }
    $full = [IO.Path]::GetFullPath($Path)
    $attributes = [IO.File]::GetAttributes($full)
    if (($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0) { throw 'Control/source must be an ordinary non-reparse file.' }
    $parent = [IO.Path]::GetDirectoryName($full)
    while ($null -ne $parent) {
        $null = Read-OrdinaryAcl $parent
        $next = [IO.Path]::GetDirectoryName($parent)
        if ([string]::IsNullOrEmpty($next) -or $next -ceq $parent) { break }
        $parent = $next
    }
    return ,([IO.File]::ReadAllBytes($full))
}
function True-Boolean($Value) { return ($Value -is [bool] -and $Value) }
function Check-Pin($Pin) {
    if ($null -eq $Pin -or $Pin.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Actual reviewed file pin required.' }
    $bytes = Read-OrdinaryBytes ([string]$Pin.path)
    if ((Sha256 $bytes) -cne $Pin.sha256) { throw 'Control/source pin differs.' }
    return ,$bytes
}
function Check-Head([string]$Repository, [string]$Expected) {
    $head = @(& git -C $Repository rev-parse --verify HEAD 2>&1)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or ([string]$head[0]).Trim() -cne $Expected) { throw 'Actual candidate HEAD differs from the operator plan.' }
}

$report = [ordered]@{
    status = 'failed'; scope = 'One local-operator initialization of an absent main CI store, no application qualification'
    host = [Environment]::MachineName; sid = $actualSid; candidateHead = $null
    createdPaths = @(); privateAcl = @(); parentExisted = $null; parentAclUnchanged = $false
    freeBytesBefore = $null; freeBytesAfter = $null; volumeAclUnchanged = $false
    existingAclModified = $false; automaticCleanup = $false; adoptedExistingTarget = $false
    qualifiesCI = $false; qualifiesDeployment = $false; after142InstalledIsOperatorAttestation = $true
    installed142Evidence = $null; sourcePins = $null; error = $null
    planSha256 = $null; authorisationSha256 = $null; initializerSha256 = $null
}
$exitCode = 1
try {
    if ([string]::IsNullOrWhiteSpace($Plan) -or [string]::IsNullOrWhiteSpace($Authorisation)) { throw 'Separate reviewed local operator plan and approval are required.' }
    $planRaw = Read-OrdinaryBytes $Plan; $authRaw = Read-OrdinaryBytes $Authorisation
    $p = [Text.Encoding]::UTF8.GetString($planRaw) | ConvertFrom-Json
    $a = [Text.Encoding]::UTF8.GetString($authRaw) | ConvertFrom-Json
    $selfSha = Sha256 (Read-OrdinaryBytes $PSCommandPath)
    if ($p.schema -ne 1 -or -not (True-Boolean $p.rootApproved) -or -not (True-Boolean $p.runAuthorised) -or
        -not (True-Boolean $p.after142Installed) -or -not (True-Boolean $a.rootApproved) -or
        -not (True-Boolean $a.runAuthorised) -or -not (True-Boolean $a.after142Installed) -or
        $a.action -cne 'initialize-main-ci-artifact-store-once' -or $a.planSha256 -cne (Sha256 $planRaw) -or
        $a.initializerSha256 -cne $selfSha -or $p.expectedHost -cne 'XSENUS' -or $p.expectedSid -cne $expectedSid -or
        $p.projectRoot -cne $projectRoot -or $p.storeRoot -cne $storeRoot -or $a.storeRoot -cne $storeRoot -or
        $p.minimumFreeBytes -ne $reserve -or $p.candidateHead -cnotmatch '^[a-f0-9]{40}$' -or
        $a.candidateHead -cne $p.candidateHead) { throw 'Exact local operator authorization, 142 prerequisite or source identity is missing.' }
    if ([string]::IsNullOrWhiteSpace([string]$p.candidateRepository) -or ([string]$p.candidateRepository) -cnotmatch '^[A-Za-z]:[\\/]' -or ([string]$p.candidateRepository).StartsWith('\\')) { throw 'Exact local absolute candidate repository required.' }
    $repository = [IO.Path]::GetFullPath([string]$p.candidateRepository).TrimEnd('\')
    $null = Read-OrdinaryAcl $repository
    if ($repository -ieq $projectRoot -or $repository.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $projectRoot.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Store must remain outside the candidate checkout.' }
    Check-Head $repository $p.candidateHead
    $expectedSources = [ordered]@{
        initializer = 'scripts\xsenus-artifact-store-init.ps1'; storeHelper = 'scripts\xsenus-artifact-store.mjs'
        trustGuard = 'scripts\verify-xsenus-runner.mjs'; workflow = '.github\workflows\deploy.yml'
    }
    if ((@($p.sourcePins.PSObject.Properties.Name | Sort-Object) -join '|') -cne (@($expectedSources.Keys | Sort-Object) -join '|')) { throw 'Exact reviewed source pin set required.' }
    foreach ($key in $expectedSources.Keys) {
        $pin = $p.sourcePins.$key
        if ([IO.Path]::GetFullPath([string]$pin.path) -ine (Join-Path $repository $expectedSources[$key])) { throw 'Source pin must refer to this exact candidate file.' }
        $null = Check-Pin $pin
    }
    if ($p.sourcePins.initializer.sha256 -cne $selfSha) { throw 'Running initializer differs from the candidate source.' }
    $null = Check-Pin $p.installed142Evidence
    if ($a.installed142EvidenceSha256 -cne $p.installed142Evidence.sha256) { throw 'Operator approval must bind the actual installed142 evidence.' }
    $report.candidateHead = $p.candidateHead; $report.installed142Evidence = $p.installed142Evidence; $report.sourcePins = $p.sourcePins
    $report.planSha256 = Sha256 $planRaw; $report.authorisationSha256 = Sha256 $authRaw; $report.initializerSha256 = $selfSha
    $drive = [IO.DriveInfo]::new('D:\')
    if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed -or @('NTFS', 'ReFS') -notcontains $drive.DriveFormat -or
        $drive.AvailableFreeSpace -lt $reserve) { throw 'D must retain the existing 20GiB plus128MiB disk reserve on a fixed security-capable volume.' }
    $report.freeBytesBefore = $drive.AvailableFreeSpace
    $volumeAcl = Read-OrdinaryAcl 'D:\'; $volumeBeforeSha = Sha256 $volumeAcl.GetSecurityDescriptorBinaryForm()
    $allowed = @($expectedSid, 'S-1-5-18', 'S-1-5-32-544')
    if ($allowed -notcontains $volumeAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw 'Unexpected volume owner.' }
    foreach ($rule in $volumeAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        $inheritOnly = ([int]$rule.PropagationFlags -band 2) -ne 0
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and -not $inheritOnly -and
            $allowed -notcontains $rule.IdentityReference.Value -and ([int64]$rule.FileSystemRights -band (64 -bor 262144 -bor 524288 -bor 268435456)) -ne 0) { throw 'Untrusted volume child-replacement or ownership permission.' }
    }
    $parentExisted = $true
    try { $null = [IO.File]::GetAttributes($projectRoot) }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { $parentExisted = $false } else { throw }
    }
    $parentBefore = $null
    if ($parentExisted) { $parentBefore = Verify-PrivateAcl $projectRoot $expectedSid }
    else { Require-Absent $projectRoot }
    Require-Absent $storeRoot
    # This existing CreateNew03 implementation accepts no racing/existing path.
    Add-Type -TypeDefinition $nativeSource -Language CSharp
    $security = New-PrivateSecurity $expectedSid; $descriptor = $security.GetSecurityDescriptorBinaryForm()
    if (-not $parentExisted) {
        [PayPlannerStoreCreateNew03]::Create($projectRoot, $descriptor)
        $report.createdPaths += $projectRoot
        $parentBefore = Verify-PrivateAcl $projectRoot $expectedSid
    }
    $report.parentExisted = $parentExisted
    Require-Absent $storeRoot
    [PayPlannerStoreCreateNew03]::Create($storeRoot, $descriptor)
    $report.createdPaths += $storeRoot
    $report.privateAcl += Verify-PrivateAcl $storeRoot $expectedSid
    $parentAfter = Verify-PrivateAcl $projectRoot $expectedSid
    if ($parentAfter.descriptorSha256 -cne $parentBefore.descriptorSha256) { throw 'Existing parent ACL changed; no repair will run.' }
    $report.parentAclUnchanged = $true
    $volumeAfterAcl = Read-OrdinaryAcl 'D:\'
    if ((Sha256 $volumeAfterAcl.GetSecurityDescriptorBinaryForm()) -cne $volumeBeforeSha) { throw 'D-root ACL changed.' }
    $report.volumeAclUnchanged = $true
    foreach ($key in $expectedSources.Keys) { $null = Check-Pin $p.sourcePins.$key }
    $null = Check-Pin $p.installed142Evidence
    if ((Sha256 (Read-OrdinaryBytes $Plan)) -cne (Sha256 $planRaw) -or
        (Sha256 (Read-OrdinaryBytes $Authorisation)) -cne (Sha256 $authRaw)) { throw 'Operator controls changed during initialization.' }
    Check-Head $repository $p.candidateHead
    $report.freeBytesAfter = ([IO.DriveInfo]::new('D:\')).AvailableFreeSpace
    if ($report.freeBytesAfter -lt $reserve) { throw 'Required disk reserve was not retained.' }
    $report.status = 'passed-local-operator-main-store-initialization-only'
    Write-NewJson (Join-Path $storeRoot 'init-local-operator.json') $report
    $exitCode = 0
} catch {
    $cause = $_.Exception.GetBaseException()
    $nativeError = if ($cause -is [ComponentModel.Win32Exception]) { $cause.NativeErrorCode } else { $null }
    $report.status = 'failed-preserve-owned-paths-no-retry'; $report.error = [ordered]@{ message = $cause.Message; nativeError = $nativeError }
}
[Console]::Out.WriteLine(($report | ConvertTo-Json -Depth 12))
exit $exitCode
