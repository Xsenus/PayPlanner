param([Parameter(Mandatory)][string]$Artifacts, [Parameter(Mandatory)][string]$RuntimeSha)

$ErrorActionPreference = 'Stop'
$Artifacts = (Resolve-Path -LiteralPath $Artifacts).Path
$archive = Join-Path $Artifacts 'cases-pilot-runtime/runtime.tar.gz'
if ((Get-FileHash -LiteralPath $archive).Hash -cne $RuntimeSha) { throw 'Comparison runtime hash mismatch.' }
$runtime = Join-Path $Artifacts 'runtime'
$null = New-Item -ItemType Directory -Path $runtime
tar -xzf $archive -C $runtime
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$proof = Get-Content -Raw -LiteralPath (Join-Path $runtime 'pilot-plan-proof.json') | ConvertFrom-Json
$tool = Join-Path $runtime 'tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll'
$manifest = Join-Path $runtime 'cases-v2-plan.json'
$sequentialRoot = Join-Path $Artifacts 'cases-pilot-sequential'
$parallelA = Join-Path $Artifacts 'cases-pilot-parallel-a'
$parallelB = Join-Path $Artifacts 'cases-pilot-parallel-b'

$combined = Join-Path $Artifacts 'parallel-union'
$null = New-Item -ItemType Directory -Path $combined
foreach ($source in @($parallelA, $parallelB)) {
    foreach ($part in Get-ChildItem -Directory -LiteralPath (Join-Path $source 'shards')) {
        $target = Join-Path $combined $part.Name
        if (Test-Path -LiteralPath $target) { throw 'The parallel runners produced overlapping shards.' }
        Copy-Item -Recurse -LiteralPath $part.FullName -Destination $target
    }
}
foreach ($resultsRoot in @((Join-Path $sequentialRoot 'shards'), $combined)) {
    dotnet $tool verify --manifest $manifest --manifest-sha $proof.manifestSha256 --results-root $resultsRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$timings = foreach ($source in @($sequentialRoot, $parallelA, $parallelB)) {
    $timing = Get-Content -Raw -LiteralPath (Join-Path $source 'timing.json') | ConvertFrom-Json -DateKind String
    if ($timing.revision -cne $proof.revision -or $timing.manifestSha256 -cne $proof.manifestSha256 -or
        $timing.assemblySha256 -cne $proof.assemblySha256) { throw 'Timing refers to a different test input.' }
    $timing
}
$serial, $a, $b = $timings
if ($a.machine -eq $b.machine) { throw 'Expected two separate parallel machines.' }
$starts = @([DateTimeOffset]::Parse($a.startedUtc), [DateTimeOffset]::Parse($b.startedUtc)) | Sort-Object
$finishes = @([DateTimeOffset]::Parse($a.finishedUtc), [DateTimeOffset]::Parse($b.finishedUtc)) | Sort-Object
if ($starts[-1] -ge $finishes[0]) { throw 'The two runner intervals did not overlap.' }
$parallelSeconds = ($finishes[-1] - $starts[0]).TotalSeconds
$comparison = [pscustomobject]@{
    revision = $proof.revision
    catalogueSha256 = $proof.catalogueSha256
    manifestSha256 = $proof.manifestSha256
    assemblySha256 = $proof.assemblySha256
    passedPerMode = $proof.cases
    sequentialSeconds = [math]::Round($serial.elapsedSeconds, 2)
    parallelSeconds = [math]::Round($parallelSeconds, 2)
    overlappingSeconds = [math]::Round(($finishes[0] - $starts[-1]).TotalSeconds, 2)
    savedSeconds = [math]::Round($serial.elapsedSeconds - $parallelSeconds, 2)
    speedup = [math]::Round($serial.elapsedSeconds / $parallelSeconds, 3)
    sameRunnerImage = @($timings.runnerImage | Sort-Object -Unique).Count -eq 1
    sameSdk = @($timings.sdk | Sort-Object -Unique).Count -eq 1
    samePostgres = @($timings.postgres | Sort-Object -Unique).Count -eq 1
    timings = $timings
}
$comparison | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $Artifacts 'pilot-comparison.json')
@(
    '## CasesV2 Windows runner pilot'
    ''
    "- Subject commit: $($comparison.revision)"
    "- Exact passed cases in each mode: $($comparison.passedPerMode)"
    "- Sequential: $($comparison.sequentialSeconds) s"
    "- Parallel: $($comparison.parallelSeconds) s"
    "- Overlap: $($comparison.overlappingSeconds) s"
    "- Speedup: $($comparison.speedup)x"
    "- Matching SDK, runner image and PostgreSQL: $($comparison.sameSdk), $($comparison.sameRunnerImage), $($comparison.samePostgres)"
) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
