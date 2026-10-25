[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Read-only host inspection. The sole writes are exclusive diagnostic reports
# and the companion's owned temporary file/link probe, which it cleans up.
# No store creation, ACL/privilege changes, service changes, DB or deployment.
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
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Expected an ordinary diagnostic script file.'
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($item.FullName)
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Write-NewJson([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 15) + [Environment]::NewLine)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Read-DirectoryAcl([string]$Path) {
    $row = [ordered]@{ path = $Path; exists = $false; isDirectory = $null; reparsePoint = $null; ownerSid = $null; groupSid = $null; accessRulesProtected = $null; rules = @(); error = $null }
    try {
        # No enumeration: inspect only this exact candidate or its named ancestor.
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $row.exists = $true
        $row.isDirectory = $item.PSIsContainer
        $row.reparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        if (-not $row.isDirectory -or $row.reparsePoint) { return $row }
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
        $row.ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $row.groupSid = $acl.GetGroup([Security.Principal.SecurityIdentifier]).Value
        $row.accessRulesProtected = $acl.AreAccessRulesProtected
        $row.rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
            [ordered]@{ sid = $_.IdentityReference.Value; type = $_.AccessControlType.ToString(); rights = [int64]$_.FileSystemRights; inherited = $_.IsInherited; inheritanceFlags = [int]$_.InheritanceFlags; propagationFlags = [int]$_.PropagationFlags }
        })
    } catch {
        if ($_.FullyQualifiedErrorId -notmatch 'PathNotFound|ItemNotFound') {
            $row.error = [ordered]@{ type = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult }
        }
    }
    return $row
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$report = [ordered]@{
    schema = 1
    scope = 'XSENUS runner token, disk and proposed artifact-store ancestors; no release qualification'
    status = 'failed'
    qualifiesCI = $false
    qualifiesDeployment = $false
    storeCreated = $false
    securityOrServiceSettingsChanged = $false
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    host = [Environment]::MachineName
    github = [ordered]@{ repository = $env:GITHUB_REPOSITORY; ref = $env:GITHUB_REF; sha = $env:GITHUB_SHA; workflowRef = $env:GITHUB_WORKFLOW_REF; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT; runnerName = $env:RUNNER_NAME }
    process = [ordered]@{ pid = $PID; username = $identity.Name; sid = $identity.User.Value; powershell = $PSVersionTable.PSVersion.ToString() }
    paths = [ordered]@{ workspace = $env:GITHUB_WORKSPACE; runnerTemp = $env:RUNNER_TEMP }
    scriptSha256 = Get-OrdinaryFileSha256 $PSCommandPath
    disks = @()
    candidateDirectories = @()
    runnerServices = @()
    processAncestry = @()
    symlinkProbe = $null
    symlinkProbeNativeExitCode = $null
    errors = @()
    limitation = 'Read-only metadata does not establish effective write access, initialize an artifact store or fix symlink rights. Child token/probe describe this unchanged runner job context; no unrelated process command lines or environment dump are collected.'
}
$identity.Dispose()
$exitCode = 1
$resultPath = $null
try {
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or $env:RUNNER_TEMP -notmatch '^[A-Za-z]:[\\/]') { throw 'An existing absolute local RUNNER_TEMP is required.' }
    $temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
    $tempItem = Get-Item -LiteralPath $temporaryRoot -Force
    if (-not $tempItem.PSIsContainer -or ($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'RUNNER_TEMP must be an ordinary directory.' }
    $resultPath = Join-Path $temporaryRoot ('payplanner-xsenus-diagnostic-' + [Guid]::NewGuid().ToString('N') + '.json')

    foreach ($letter in @('C', 'D', 'E')) {
        $drivePath = $letter + ':\'
        $drive = [IO.DriveInfo]::new($drivePath)
        $row = [ordered]@{ root = $drivePath; ready = $drive.IsReady; driveType = $drive.DriveType.ToString(); filesystem = $null; availableFreeBytes = $null; totalFreeBytes = $null; sizeBytes = $null }
        if ($drive.IsReady) {
            $row.filesystem = $drive.DriveFormat
            $row.availableFreeBytes = $drive.AvailableFreeSpace
            $row.totalFreeBytes = $drive.TotalFreeSpace
            $row.sizeBytes = $drive.TotalSize
        }
        $report.disks += $row
        if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) { continue }
        $inspectNextAncestor = $true
        foreach ($suffix in @('', 'ProjectTemp', 'ProjectTemp\PayPlanner', 'ProjectTemp\PayPlanner\ci-artifacts')) {
            $path = if ($suffix) { Join-Path $drivePath $suffix } else { $drivePath }
            if (-not $inspectNextAncestor) {
                $report.candidateDirectories += [ordered]@{ path = $path; inspected = $false; reason = 'An earlier ancestor was missing, not an ordinary directory or unreadable; no traversal performed.'; error = $null }
                continue
            }
            $directoryRow = Read-DirectoryAcl $path
            $report.candidateDirectories += $directoryRow
            $inspectNextAncestor = $directoryRow.exists -and $directoryRow.isDirectory -and -not $directoryRow.reparsePoint -and $null -eq $directoryRow.error
        }
    }

    try {
        # Query only this process's bounded ancestry, without command lines.
        $ancestorPid = $PID
        $seen = @{}
        for ($depth = 0; $depth -lt 10 -and $ancestorPid -gt 0; $depth++) {
            if ($seen.ContainsKey($ancestorPid)) { break }
            $seen[$ancestorPid] = $true
            $item = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId=' + $ancestorPid) -Property ProcessId,ParentProcessId,Name
            if ($null -eq $item) { break }
            $report.processAncestry += [ordered]@{ pid = $item.ProcessId; parentPid = $item.ParentProcessId; name = $item.Name }
            $ancestorPid = [int]$item.ParentProcessId
        }
        $report.runnerServices = @(Get-CimInstance -ClassName Win32_Service -Filter "Name LIKE 'actions.runner.%'" -Property Name,State,StartName,ProcessId | ForEach-Object {
            $sid = $null
            try {
                $account = $_.StartName
                if ($account -eq 'LocalSystem') { $sid = 'S-1-5-18' }
                else {
                    if ($account.StartsWith('.\')) { $account = [Environment]::MachineName + $account.Substring(1) }
                    $sid = ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier]).Value
                }
            } catch { }
            [ordered]@{ name = $_.Name; state = $_.State; account = $_.StartName; sid = $sid; processId = $_.ProcessId; matchesObservedAncestry = $seen.ContainsKey([int]$_.ProcessId) }
        })
    } catch {
        $report.errors += [ordered]@{ stage = 'runner-service-metadata'; type = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult }
    }

    $probeScript = Join-Path $PSScriptRoot 'xsenus-symlink-diagnostic.ps1'
    if ((Get-OrdinaryFileSha256 $probeScript) -cne '638f30e87895298841481fb53be00073587b26914d69bc277df6f40f1dfa59a6') { throw 'Pinned token/probe script differs.' }
    $probeDirectory = Join-Path $temporaryRoot ('payplanner-token-probe-' + [Guid]::NewGuid().ToString('N'))
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $probeOutput = & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -File $probeScript -OutputDirectory $probeDirectory
    $report.symlinkProbeNativeExitCode = $LASTEXITCODE
    $report.symlinkProbe = ($probeOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($report.symlinkProbe.host -cne $report.host -or $report.symlinkProbe.processToken.sid -cne $report.process.sid) { throw 'Unexpected probe process identity.' }
    $aclErrors = @($report.candidateDirectories | Where-Object { $null -ne $_.error })
    if ($report.symlinkProbeNativeExitCode -eq 0 -and $report.symlinkProbe.status -eq 'passed' -and $aclErrors.Count -eq 0 -and $report.errors.Count -eq 0) {
        $exitCode = 0
        $report.status = 'passed'
    }
} catch {
    # No arbitrary process/environment text; retain structured failure identity.
    $report.errors += [ordered]@{ type = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult; message = $_.Exception.Message }
}
$report.finishedUtc = [DateTime]::UtcNow.ToString('o')
if ($null -ne $resultPath) { Write-NewJson $resultPath $report }
[Console]::Out.WriteLine(($report | ConvertTo-Json -Depth 15))
exit $exitCode
