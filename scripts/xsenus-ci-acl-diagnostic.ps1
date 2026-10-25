[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Read-only ACL metadata for an explicit path set. The only write is a new report.
# No enumeration, symlink probe, ACL/privilege/service mutation, store, DB or release.
$expectedRef = 'refs/heads/claude/practical-bardeen-pglcf3'
if ([Environment]::MachineName.ToUpperInvariant() -cne 'XSENUS' -or
    $env:GITHUB_REPOSITORY -cne 'Xsenus/PayPlanner' -or
    $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_REF -cne $expectedRef -or
    $env:GITHUB_WORKFLOW_REF -cne ('Xsenus/PayPlanner/.github/workflows/deploy.yml@' + $expectedRef)) {
    throw 'Only the explicitly reviewed XSENUS diagnostic workflow context is allowed.'
}

function Get-OrdinaryFileSha256([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Expected an ordinary file.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($item.FullName)
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Read-DirectoryAcl([string]$Path) {
    $row = [ordered]@{ path = $Path; inspected = $true; exists = $false; isDirectory = $null; reparsePoint = $null; aclRead = $false; ownerSid = $null; groupSid = $null; accessRulesProtected = $null; rules = @(); error = $null }
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $row.exists = $true
        $row.isDirectory = $item.PSIsContainer
        $row.reparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        if (-not $row.isDirectory -or $row.reparsePoint) { return $row }
        # Windows PowerShell 5/.NET Framework: avoid unavailable Get-Acl module.
        # Access, owner and group only; never request SACL or any write operation.
        $sections = [Security.AccessControl.AccessControlSections]::Access -bor [Security.AccessControl.AccessControlSections]::Owner -bor [Security.AccessControl.AccessControlSections]::Group
        $acl = ([IO.DirectoryInfo]::new($Path)).GetAccessControl($sections)
        $row.ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $row.groupSid = $acl.GetGroup([Security.Principal.SecurityIdentifier]).Value
        $row.accessRulesProtected = $acl.AreAccessRulesProtected
        $row.rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
            [ordered]@{ sid = $_.IdentityReference.Value; type = $_.AccessControlType.ToString(); rights = [int64]$_.FileSystemRights; inherited = $_.IsInherited; inheritanceFlags = [int]$_.InheritanceFlags; propagationFlags = [int]$_.PropagationFlags }
        })
        $row.aclRead = $true
    } catch {
        if ($_.FullyQualifiedErrorId -notmatch 'PathNotFound|ItemNotFound') {
            $row.error = [ordered]@{ type = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult }
        }
    }
    return $row
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$report = [ordered]@{
    schema = 2
    scope = 'XSENUS fixed artifact-store candidate ACL metadata only'
    status = 'failed'
    qualifiesCI = $false
    qualifiesDeployment = $false
    qualifiesArtifactStore = $false
    effectiveWriteAccessTested = $false
    storeCreated = $false
    securityOrServiceSettingsChanged = $false
    symlinkProbeExecuted = $false
    priorSymlinkEvidence = [ordered]@{ runId = '40246305777'; nativeExitCode = 1; win32Error = 1314; observedJsonSha256 = '97c104ad0b4f2d9d5c81c863ec0bde95535aa9e66d2ec2cdfc20721874064f2d'; interpretation = 'Historical failure retained; this ACL-only run cannot replace or qualify the symlink result.' }
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    host = [Environment]::MachineName
    github = [ordered]@{ repository = $env:GITHUB_REPOSITORY; ref = $env:GITHUB_REF; sha = $env:GITHUB_SHA; workflowRef = $env:GITHUB_WORKFLOW_REF; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT; runnerName = $env:RUNNER_NAME }
    process = [ordered]@{ pid = $PID; username = $identity.Name; sid = $identity.User.Value; powershell = $PSVersionTable.PSVersion.ToString() }
    scriptSha256 = Get-OrdinaryFileSha256 $PSCommandPath
    candidateDirectories = @()
    errors = @()
    limitation = 'Exit zero means bounded metadata collection completed. Missing/reparse ancestors stop inspection. ACL metadata does not establish effective write access or qualify a store; no symlink test is executed.'
}
$identity.Dispose()
$exitCode = 1
$resultPath = $null
try {
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or $env:RUNNER_TEMP -notmatch '^[A-Za-z]:[\\/]') { throw 'An existing absolute local RUNNER_TEMP is required.' }
    $temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
    $tempItem = Get-Item -LiteralPath $temporaryRoot -Force
    if (-not $tempItem.PSIsContainer -or ($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'RUNNER_TEMP must be an ordinary directory.' }
    $resultPath = Join-Path $temporaryRoot ('payplanner-xsenus-acl-diagnostic-' + [Guid]::NewGuid().ToString('N') + '.json')
    $chains = @(
        @('D:\', 'D:\PayPlannerCIArtifacts'),
        @('D:\', 'D:\ProjectData', 'D:\ProjectData\PayPlanner', 'D:\ProjectData\PayPlanner\ci-artifacts'),
        @('D:\', 'D:\ProjectTemp', 'D:\ProjectTemp\PayPlanner', 'D:\ProjectTemp\PayPlanner\ci-artifacts')
    )
    $observed = @{}
    foreach ($chain in $chains) {
        $inspectNextAncestor = $true
        foreach ($path in $chain) {
            if ($observed.ContainsKey($path)) { $row = $observed[$path] }
            elseif (-not $inspectNextAncestor) {
                $row = [ordered]@{ path = $path; inspected = $false; exists = $null; isDirectory = $null; reparsePoint = $null; aclRead = $false; error = $null; reason = 'An earlier ancestor was missing, not an ordinary directory, reparse or unreadable; no traversal performed.' }
                $observed[$path] = $row
                $report.candidateDirectories += $row
            } else {
                $row = Read-DirectoryAcl $path
                $observed[$path] = $row
                $report.candidateDirectories += $row
            }
            $inspectNextAncestor = $inspectNextAncestor -and $row.inspected -and $row.exists -and $row.isDirectory -and -not $row.reparsePoint -and $row.aclRead -and $null -eq $row.error
        }
    }
    $aclErrors = @($report.candidateDirectories | Where-Object { $null -ne $_.error })
    if ($observed['D:\'].aclRead -and $aclErrors.Count -eq 0) { $exitCode = 0; $report.status = 'passed' }
} catch {
    $report.errors += [ordered]@{ type = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult; message = $_.Exception.Message }
}
$report.finishedUtc = [DateTime]::UtcNow.ToString('o')
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 15) + [Environment]::NewLine)
if ($null -ne $resultPath) {
    $stream = [IO.File]::Open($resultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
[Console]::Out.WriteLine([Text.Encoding]::UTF8.GetString($bytes))
exit $exitCode
