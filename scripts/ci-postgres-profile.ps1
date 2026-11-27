$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or !$env:PAYPLANNER_TEST_POSTGRES) {
    throw 'Expected the disposable PostgreSQL cluster on a hosted runner.'
}
$testConnection = [Data.Common.DbConnectionStringBuilder]::new()
$testConnection.ConnectionString = $env:PAYPLANNER_TEST_POSTGRES
if ($testConnection['Host'] -ne '127.0.0.1' -or $testConnection['Port'] -ne '5433' -or
    $testConnection['Database'] -ne 'postgres' -or $testConnection['Username'] -ne 'payplanner_ci') {
    throw 'Unexpected PostgreSQL endpoint.'
}

function Invoke-ProfileSql([string]$Database, [string]$Query) {
    $output = & $env:PAYPLANNER_TEST_PSQL -X -w -qAt -h 127.0.0.1 -p 5433 -U payplanner_ci -d $Database -v ON_ERROR_STOP=1 -c $Query
    if ($LASTEXITCODE -ne 0) { throw "Profile SQL failed on $Database." }
    return $output
}

$template = 'pp_profile_template'
$tableCount = 186
$repetitions = 12
$env:PGPASSWORD = $testConnection['Password']
try {
    $settings = Invoke-ProfileSql 'postgres' "SELECT current_setting('server_version') || '|' || current_setting('fsync') || '|' || current_setting('full_page_writes') || '|' || current_setting('synchronous_commit')"
    $null = Invoke-ProfileSql 'postgres' "CREATE DATABASE $template TEMPLATE template0"
    $null = Invoke-ProfileSql $template @'
DO $setup$
BEGIN
  FOR table_number IN 1..186 LOOP
    EXECUTE format('CREATE TABLE sample_%s (id int PRIMARY KEY, parent_id int NOT NULL, payload text NOT NULL)', table_number);
    EXECUTE format('CREATE INDEX ON sample_%s (parent_id)', table_number);
    EXECUTE format('INSERT INTO sample_%s SELECT i, i %% 3, repeat(md5(i::text), 4) FROM generate_series(1, 8) i', table_number);
  END LOOP;
END;
$setup$;
'@
    $sourceBytes = [long](Invoke-ProfileSql 'postgres' "SELECT pg_database_size('$template')")
    $null = Invoke-ProfileSql 'postgres' "ALTER DATABASE $template ALLOW_CONNECTIONS false"
    $samples = foreach ($round in 1..$repetitions) {
        $strategies = if ($round % 2 -eq 0) { @('FILE_COPY', 'WAL_LOG') } else { @('WAL_LOG', 'FILE_COPY') }
        foreach ($strategy in $strategies) {
            $database = "pp_profile_$($strategy.ToLowerInvariant())_$round"
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $null = Invoke-ProfileSql 'postgres' "CREATE DATABASE $database TEMPLATE $template STRATEGY $strategy"
            $create = $watch.Elapsed.TotalMilliseconds
            try {
                $actualTableCount = [int](Invoke-ProfileSql $database "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")
                $existingRows = [int](Invoke-ProfileSql $database 'SELECT count(*) FROM sample_1')
                if ($actualTableCount -ne $tableCount -or $existingRows -ne 8) { throw 'The clone lost template tables or rows.' }
                $null = Invoke-ProfileSql $database 'INSERT INTO sample_1 VALUES (1000, 0, ''own clone row'')'
            } finally {
                $watch.Restart()
                $null = Invoke-ProfileSql 'postgres' "DROP DATABASE $database"
                $drop = $watch.Elapsed.TotalMilliseconds
            }
            [pscustomobject]@{ round = $round; strategy = $strategy; createMs = $create; dropMs = $drop }
        }
    }
    $null = Invoke-ProfileSql 'postgres' "ALTER DATABASE $template ALLOW_CONNECTIONS true"
    if ([int](Invoke-ProfileSql $template 'SELECT count(*) FROM sample_1') -ne 8) { throw 'A clone modified the template.' }
    $null = Invoke-ProfileSql 'postgres' "DROP DATABASE $template"
    $remaining = Invoke-ProfileSql 'postgres' "SELECT count(*) FROM pg_database WHERE datname LIKE 'pp_profile_%'"
    if ([int]$remaining -ne 0 -or $samples.Count -ne $repetitions * 2) { throw 'The experiment is incomplete.' }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

$byStrategy = foreach ($group in $samples | Group-Object strategy) {
    $createMs = @($group.Group.createMs | Sort-Object)
    $dropMs = @($group.Group.dropMs | Sort-Object)
    $middle = [int]($group.Count / 2)
    [pscustomobject]@{
        strategy = $group.Name
        count = $group.Count
        medianCreateMs = [math]::Round(($createMs[$middle - 1] + $createMs[$middle]) / 2, 2)
        medianDropMs = [math]::Round(($dropMs[$middle - 1] + $dropMs[$middle]) / 2, 2)
    }
}
[pscustomobject]@{
    image = $env:ImageVersion
    postgresSettings = $settings
    syntheticTableCount = $tableCount
    syntheticTemplateBytes = $sourceBytes
    medians = @($byStrategy)
    samples = @($samples)
} | ConvertTo-Json -Depth 5 | Tee-Object -FilePath 'postgres-profile.json'
