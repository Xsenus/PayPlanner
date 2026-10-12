param([string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
$testCluster = [IO.Path]::GetFullPath((Join-Path $Repository '.codex-build/claude-test-postgres'))
$initdbTool = (Get-Command initdb.exe -ErrorAction Stop).Source
$pgctlTool = (Get-Command pg_ctl.exe -ErrorAction Stop).Source
if (Test-Path -LiteralPath $testCluster) { throw 'Каталог уже существует: скрипт не меняет существующий кластер. Используйте другой checkout.' }
if (Get-NetTCPConnection -LocalPort 55432 -State Listen -ErrorAction SilentlyContinue) { throw 'Порт 55432 занят; чужой сервер не использовать.' }
$parentDirectory = [IO.Directory]::CreateDirectory((Split-Path $testCluster -Parent))
for ($directory = $parentDirectory; $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Каталог кластера не должен проходить через junction/symlink.' }
}
& $initdbTool -D $testCluster -U postgres --auth-local=trust --auth-host=trust --encoding=UTF8
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать пустой кластер.' }
# Only disposable synthetic tests: no persistence/recovery qualification here.
Add-Content -LiteralPath (Join-Path $testCluster 'postgresql.conf') -Value "`nfsync = off`nsynchronous_commit = off`nfull_page_writes = off"
$serverLog = Join-Path $testCluster 'server.log'
$startArguments = @('-D', ('"{0}"' -f $testCluster), '-l', ('"{0}"' -f $serverLog), '-o', '"-h 127.0.0.1 -p 55432"', '-w', 'start')
# Wait only for pg_ctl, not the persistent postgres process tree or inherited pipes.
$startProcess = Start-Process -FilePath $pgctlTool -ArgumentList $startArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testCluster 'start.out.log') -RedirectStandardError (Join-Path $testCluster 'start.err.log')
$startProcess.WaitForExit()
if ($startProcess.ExitCode -ne 0) { throw 'PostgreSQL не запустилась; см. start.out.log/start.err.log.' }
$env:PAYPLANNER_TEST_POSTGRES = 'Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Pooling=false;Command Timeout=180'
$env:BackgroundServices__Enabled = 'false'
$env:Email__Enabled = 'false'
Write-Output 'Пустой PostgreSQL доступен только на 127.0.0.1:55432; доверенная локальная авторизация, только синтетические тесты.'
Write-Output 'Для остановки: pg_ctl.exe -D .codex-build/claude-test-postgres -m fast -w stop'
