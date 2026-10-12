param([string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
$syntheticRoot = Join-Path $Repository '.codex-build/claude-synthetic'
$syntheticConfig = Join-Path $syntheticRoot 'runtime.json'
if (-not (Test-Path -LiteralPath $syntheticConfig)) { throw 'Сначала запустите инструмент Seed.' }
$syntheticValues = Get-Content -LiteralPath $syntheticConfig -Raw | ConvertFrom-Json
if ($syntheticValues.BackgroundServices__Enabled -ne 'false' -or $syntheticValues.Email__Enabled -ne 'false' -or $syntheticValues.RestoredRuntimeIsolation__Enabled -ne 'true') {
    throw 'Изоляция, почта и фон должны оставаться в безопасном состоянии.'
}
if ($syntheticValues.ASPNETCORE_URLS -ne 'http://127.0.0.1:5300' -or $syntheticValues.Database__Provider -ne 'sqlite' -or $syntheticValues.ASPNETCORE_ENVIRONMENT -ne 'ClaudeSynthetic') { throw 'Разрешена только локальная синтетическая SQLite-среда.' }
$expectedDatabase = [IO.Path]::GetFullPath((Join-Path $syntheticRoot 'synthetic.db'))
if ($syntheticValues.ConnectionStrings__Default -ne ('Data Source=' + $expectedDatabase + ';Mode=ReadWriteCreate')) { throw 'Разрешена только созданная инструментом база synthetic.db в этом checkout.' }
if (-not (Test-Path -LiteralPath (Join-Path $syntheticRoot 'seed-summary.json'))) { throw 'Сидирование не завершено.' }
foreach ($property in $syntheticValues.PSObject.Properties) { [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process') }
$env:DataProtection__ApplicationName = 'PayPlanner.Claude.Synthetic'
$env:BackgroundServices__Enabled = 'false'
$env:Email__Enabled = 'false'
$env:MessengerDocumentDelivery__Enabled = 'false'
$env:MessengerDocumentDelivery__Providers__Max__Enabled = 'false'
$env:ElectronicSignature__Enabled = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Output 'Среда: synthetic SQLite, loopback, фон/почта/внешние обращения выключены.'
