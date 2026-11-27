$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ((git rev-parse HEAD) -ne $env:SUBJECT_SHA) {
    throw 'The application revision does not match the pilot'
}

$runEndpoint = "repos/$env:GITHUB_REPOSITORY/actions/runs/$env:PRIOR_RUN"
$run = gh api $runEndpoint | ConvertFrom-Json
if ($run.head_repository.full_name -cne $env:GITHUB_REPOSITORY -or
    $run.head_sha -cne $env:PRIOR_WORKFLOW_SHA -or
    $run.head_branch -cne 'ci/full-release-ci-pilot' -or
    $run.path -cne '.github/workflows/full-release-ci-pilot.yml' -or
    $run.event -cne 'push') {
    throw 'The previous run does not match the pinned pilot workflow'
}

$jobs = gh api "$runEndpoint/attempts/1/jobs?per_page=100" | ConvertFrom-Json
$matching = @($jobs.jobs | Where-Object { $_.name -ceq $env:GITHUB_JOB })
if ($matching.Count -ne 1) {
    throw "Expected one previous job named $env:GITHUB_JOB"
}
$job = $matching[0]
if ($job.status -eq 'completed' -and $job.conclusion -eq 'success' -and
    $job.runner_group_name -eq 'GitHub Actions' -and $job.labels -contains 'windows-2025') {
    "reused=true" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "Reused the complete $($job.name) gate on $env:SUBJECT_SHA from $($job.html_url)"
} else {
    "reused=false" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "The previous $($job.name) gate has no qualifying result"
}
