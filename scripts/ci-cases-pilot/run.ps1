param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$Results,
    [Parameter(Mandatory)][string]$Shards
)

$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or !$env:PAYPLANNER_TEST_POSTGRES -or
    $env:Email__Enabled -ne 'false' -or $env:BackgroundServices__Enabled -ne 'false') {
    throw 'Hosted execution requires the isolated database and disabled email and background services.'
}
$Runtime = (Resolve-Path -LiteralPath $Runtime).Path
$Results = [IO.Path]::GetFullPath($Results)
$tool = Join-Path $Runtime 'tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll'
$manifest = Join-Path $Runtime 'cases-v2-plan.json'
$proof = Get-Content -Raw -LiteralPath (Join-Path $Runtime 'pilot-plan-proof.json') | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $manifest).Hash -cne $proof.manifestSha256) { throw 'Pilot manifest hash mismatch.' }
$parts = $Shards.Split(',')
if (($parts | Where-Object { $_ -notmatch '^[0-3]$' }) -or ($parts | Sort-Object -Unique).Count -ne $parts.Count) {
    throw 'Invalid shard assignment.'
}
$null = New-Item -ItemType Directory -Path (Join-Path $Results 'shards')
$venv = & dotnet --version
if ($LASTEXITCODE -ne 0 -or $venv -cne $env:PILOT_SDK) { throw 'Unexpected .NET SDK.' }
$pgVersion = & $env:PAYPLANNER_TEST_PSQL --version
if ($LASTEXITCODE -ne 0) { throw 'Could not fingerprint PostgreSQL.' }
$started = [DateTimeOffset]::UtcNow
$watch = [Diagnostics.Stopwatch]::StartNew()
foreach ($part in $parts) {
    $destination = Join-Path $Results "shards/shard-$part"
    Write-Output "::group::Run shard $part"
    & dotnet $tool run --manifest $manifest --manifest-sha $proof.manifestSha256 --shard $part --results $destination --workers 1 --suite all
    $code = $LASTEXITCODE
    Write-Output '::endgroup::'
    if ($code -ne 0) { throw "Shard $part failed with native exit $code." }
}
$watch.Stop()
$finished = [DateTimeOffset]::UtcNow
[pscustomobject]@{
    revision = $proof.revision
    manifestSha256 = $proof.manifestSha256
    assemblySha256 = $proof.assemblySha256
    machine = $env:COMPUTERNAME
    runnerImage = $env:ImageVersion
    processors = [Environment]::ProcessorCount
    sdk = $venv
    postgres = $pgVersion
    shards = $parts
    startedUtc = $started.ToString('o')
    finishedUtc = $finished.ToString('o')
    elapsedSeconds = $watch.Elapsed.TotalSeconds
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Results 'timing.json')
