param([Parameter(Mandatory)][string]$Verifier)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or !$env:PGDATA -or !$env:PGBIN) {
    throw 'Expected the disposable GitHub-hosted Windows PostgreSQL image.'
}
$service = 'postgresql-x64-17'
$psql = Join-Path $env:PGBIN 'psql.exe'
$configuration = Join-Path $env:PGDATA 'postgresql.conf'
$authentication = Join-Path $env:PGDATA 'pg_hba.conf'
if ((Get-Service -Name $service).Status -ne 'Stopped') { throw 'Expected a stopped, fresh PostgreSQL service.' }
Add-Content -LiteralPath $configuration -Value @('', "listen_addresses = '127.0.0.1'", 'port = 5433')
$existing = Get-Content -Raw -LiteralPath $authentication
[IO.File]::WriteAllText($authentication, "host payplanner payplanner_ci 127.0.0.1/32 reject`n" + $existing)
Set-Service -Name $service -StartupType Manual
Start-Service -Name $service

$psqlArgs = @('-X', '-w', '-h', '127.0.0.1', '-p', '5433', '-U', 'postgres', '-d', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1')
$env:PGPASSWORD = 'root'
try {
    $databases = & $psql @psqlArgs -c "SELECT string_agg(datname, ',' ORDER BY datname) FROM pg_database"
    if ($LASTEXITCODE -ne 0 -or $databases -cne 'postgres,template0,template1') {
        throw 'Expected only the initial databases on the hosted runner.'
    }
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    Write-Output "::add-mask::$password"
    & $psql @psqlArgs -c "CREATE ROLE payplanner_ci LOGIN CREATEDB PASSWORD '$password'"
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the disposable test role.' }
    & $psql @psqlArgs -c 'SHOW server_version'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the PostgreSQL version.' }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
$env:PAYPLANNER_TEST_POSTGRES = "Host=127.0.0.1;Port=5433;Database=postgres;Username=payplanner_ci;Password=$password;Pooling=false"
& node --input-type=module -e 'const { verifyPostgres } = await import(new URL(process.argv[1])); verifyPostgres();' ([Uri](Resolve-Path -LiteralPath $Verifier).Path).AbsoluteUri
if ($LASTEXITCODE -ne 0) { throw 'The repository PostgreSQL isolation preflight failed.' }
@(
    "PAYPLANNER_TEST_POSTGRES=$env:PAYPLANNER_TEST_POSTGRES"
    "PAYPLANNER_TEST_PG_DUMP=$(Join-Path $env:PGBIN 'pg_dump.exe')"
    "PAYPLANNER_TEST_PSQL=$psql"
) | Add-Content -LiteralPath $env:GITHUB_ENV
