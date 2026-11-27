param([Parameter(Mandatory)][string]$Runtime, [Parameter(Mandatory)][string]$Revision)

$ErrorActionPreference = 'Stop'
$Runtime = (Resolve-Path -LiteralPath $Runtime).Path
$fullPath = Join-Path $Runtime 'test-plan.json'
$toolPath = Join-Path $Runtime 'tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll'
$tool = [Reflection.Assembly]::LoadFrom($toolPath)
$planApi = $tool.GetType('PayPlanner.TestShards.ShardPlan', $true)
$fullHash = (Get-FileHash -LiteralPath $fullPath).Hash
$full = $planApi.GetMethod('Read').Invoke($null, [object[]]@([string]$fullPath, [string]$fullHash))
if ($full.Revision -cne $Revision -or $full.Cases.Length -ne 20216) {
    throw 'The full discovery differs from the reference revision and catalogue.'
}
$planApi.GetMethod('VerifyArtifacts').Invoke($null, [object[]]@($full.PSObject.BaseObject, [string]$fullPath))

$namePrefix = 'PayPlanner.Api.Tests.CasesV2ControllerTests.'
$selected = @($full.Cases | Where-Object { $_.FullyQualifiedName.StartsWith($namePrefix, [StringComparison]::Ordinal) })
$identities = [string[]]@($selected | ForEach-Object { $_.FullyQualifiedName + [char]0 + $_.DisplayName })
[Array]::Sort($identities, [StringComparer]::Ordinal)
$bytes = [Text.Encoding]::UTF8.GetBytes(($identities -join "`n"))
$identityHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
$expectedHash = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reference-catalogue.sha256') -Raw).Trim()
if ($selected.Length -ne 4720 -or $identityHash -cne $expectedHash) {
    throw "CasesV2 discovery differs from the saved reference (count $($selected.Length), SHA-256 $identityHash)."
}

$caseType = $tool.GetType('PayPlanner.TestShards.DiscoveredCase', $true)
$constructor = $caseType.GetConstructors() | Where-Object IsPublic | Select-Object -First 1
$cases = [Array]::CreateInstance($caseType, $selected.Length)
for ($index = 0; $index -lt $selected.Length; $index++) {
    $row = $selected[$index]
    $cases.SetValue($constructor.Invoke([object[]]@([Guid]$row.Id, [string]$row.FullyQualifiedName, [string]$row.DisplayName)), $index)
}
$arguments = [object[]]::new(7)
$arguments[0] = $full.Revision
$arguments[1] = $full.AssemblyPath
$arguments[2] = $full.Artifacts
$arguments[3] = $cases
$arguments[4] = $full.ShardCount
$arguments[5] = $full.Timings
$arguments[6] = $full.Execution
$plan = $planApi.GetMethod('Create').Invoke($null, $arguments)
$target = Join-Path $Runtime 'cases-v2-plan.json'
$jsonOptions = $planApi.GetField('Json').GetValue($null)
[IO.File]::WriteAllText($target, [Text.Json.JsonSerializer]::Serialize($plan, $plan.GetType(), $jsonOptions))
$manifestHash = (Get-FileHash -LiteralPath $target).Hash
$null = $planApi.GetMethod('Read').Invoke($null, [object[]]@([string]$target, [string]$manifestHash))

[pscustomobject]@{
    revision = $Revision
    fullManifestSha256 = $fullHash
    manifestSha256 = $manifestHash
    catalogueSha256 = $identityHash
    assemblySha256 = ($plan.Artifacts | Where-Object Path -CEQ $plan.AssemblyPath).Sha256
    cases = $plan.Cases.Length
    shards = @($plan.Cases | Group-Object Shard | Sort-Object Name | ForEach-Object Count)
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $Runtime 'pilot-plan-proof.json')
