$ErrorActionPreference = 'Stop'
$source = Join-Path $PWD 'subject'
$results = Join-Path $PWD 'sqlite-results'
$testFile = 'backend/PayPlanner.Api.Tests/CasesV2ControllerTests.cs'
$project = Join-Path $source 'backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj'
$dll = Join-Path $source 'backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll'
$resultDirectory = New-Item -ItemType Directory -Path $results
$settings = Join-Path $resultDirectory.FullName 'gate.runsettings'
Set-Content -LiteralPath $settings -Value '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>'

$class = 'PayPlanner.Api.Tests.CasesV2ControllerTests'
$sample = @(
    'CourtGraph_HttpRequiresCurrentActorAndVisibleEditableCase'
    'CourtNullable_HttpPreservesForeignReferenceForEachMode'
    'CourtRetention_HttpAppliesModePreservingForeignDependents'
    'ResolveCaseNotificationRecipientIdsAsync_UsesFixedQueryCountForSeveralRecipients'
)

function Invoke-ExactCases([string]$name, [string[]]$methods, [int]$expected) {
    $filter = ($methods | ForEach-Object { "FullyQualifiedName=$class.$_" }) -join '|'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet vstest $dll "/Settings:$settings" "/TestCaseFilter:$filter" "/Logger:trx;LogFileName=$name.trx" "/ResultsDirectory:$results" | Out-Host
    $code = $LASTEXITCODE
    $timer.Stop()
    if ($code -ne 0) { throw "$name failed with exit $code." }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results "$name.trx") -Raw
    $rows = @($trx.TestRun.Results.UnitTestResult)
    if ($rows.Length -ne $expected -or @($rows | Where-Object outcome -CNE 'Passed').Count -ne 0) {
        throw "$name did not pass the expected $expected cases."
    }
    $keys = @($rows | ForEach-Object { "$($_.testId) $($_.testName)" } | Sort-Object -CaseSensitive)
    if (@($keys | Sort-Object -Unique -CaseSensitive).Count -ne $expected) { throw "$name has duplicate identities." }
    return [pscustomobject]@{ seconds = $timer.Elapsed.TotalSeconds; keys = $keys; count = $rows.Length }
}

Push-Location $source
try {
    $baseHash = (Get-FileHash -LiteralPath $dll).Hash
    $before = Invoke-ExactCases 'baseline' $sample 153
    $originalSourceHash = (Get-FileHash -LiteralPath $testFile).Hash
    Copy-Item -LiteralPath (Join-Path $env:GITHUB_WORKSPACE $testFile) -Destination $testFile
    $candidate = Get-Item -LiteralPath $testFile
    if ((Get-FileHash -LiteralPath $candidate.FullName).Hash -ceq $originalSourceHash) {
        throw 'The candidate source matches the baseline.'
    }
    # Copy-Item preserves the checkout timestamp. Mark the actual changed source for MSBuild.
    $candidate.LastWriteTimeUtc = [DateTime]::UtcNow
    & dotnet build $project --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $candidateHash = (Get-FileHash -LiteralPath $dll).Hash
    if ($candidateHash -ceq $baseHash) { throw 'The optimization was not compiled.' }
    $after = Invoke-ExactCases 'candidate' $sample 153
    if (Compare-Object -CaseSensitive -ReferenceObject $before.keys -DifferenceObject $after.keys) {
        throw 'The optimization changed the tested case identities.'
    }
    $rollback = Invoke-ExactCases 'rollback' @('CourtGraph_FailedSaveOrCancellationRollsBackAllCoreRecords') 152
} finally {
    Pop-Location
}
[pscustomobject]@{
    baselineRevision = $env:SUBJECT_BASE
    candidateRevision = $env:GITHUB_SHA
    baselineAssemblySha256 = $baseHash
    candidateAssemblySha256 = $candidateHash
    comparedCases = $after.count
    rollbackPassed = $rollback.count
    baselineSeconds = [math]::Round($before.seconds, 2)
    candidateSeconds = [math]::Round($after.seconds, 2)
    savedSeconds = [math]::Round($before.seconds - $after.seconds, 2)
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $results 'comparison.json')
