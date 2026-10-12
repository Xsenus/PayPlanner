param([string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Use-SyntheticEnvironment.ps1') -Repository $Repository
$headers = @{ 'X-PayPlanner-Restored-Instance' = $env:RestoredRuntimeIsolation__InstanceId }
$identity = Invoke-RestMethod 'http://127.0.0.1:5300/api/restored-runtime/identity'
if (-not $identity.isolated -or $identity.instanceId -ne $env:RestoredRuntimeIsolation__InstanceId) { throw 'Не совпадает идентичность синтетической среды.' }
if ((Invoke-WebRequest 'http://127.0.0.1:5300/api/health' -Headers $headers).StatusCode -ne 200) { throw 'Backend health failed.' }
if ((Invoke-WebRequest 'http://127.0.0.1:5300/api/health' -SkipHttpErrorCheck).StatusCode -ne 409) { throw 'Не сработала защита запроса без instance header.' }
$actors = Get-Content -LiteralPath (Join-Path $Repository '.codex-build/claude-synthetic/credentials.local.json') -Raw | ConvertFrom-Json
if ($actors.Count -ne 5) { throw 'Ожидаются пять синтетических ролей.' }
foreach ($actor in $actors) {
    $body = @{ email = $actor.email; password = $actor.password } | ConvertTo-Json -Compress
    $session = Invoke-RestMethod 'http://127.0.0.1:5300/api/auth/login' -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    if (-not $session.token) { throw ('Не выдана локальная сессия для ' + $actor.role) }
    Write-Output ($actor.role + ': локальный вход OK')
}
if ((Invoke-WebRequest 'http://127.0.0.1:5301/').StatusCode -ne 200) { throw 'Frontend не запущен на 5301.' }
Write-Output 'Health/identity, отказ без header, пять входов, frontend HTTP: OK. Пароли и токены не выводятся.'
