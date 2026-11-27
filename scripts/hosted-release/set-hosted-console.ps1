$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Expected an ephemeral GitHub-hosted Windows runner'
}
if (!(Test-Path HKCU:\Console)) { New-Item HKCU:\Console | Out-Null }
New-ItemProperty -Path HKCU:\Console -Name CodePage -Value 65001 -PropertyType DWord -Force | Out-Null
$application = (Get-Command dotnet).Source.Replace('\', '_')
$key = Join-Path HKCU:\Console $application
if (!(Test-Path -LiteralPath $key)) { New-Item -Path $key | Out-Null }
New-ItemProperty -LiteralPath $key -Name CodePage -Value 65001 -PropertyType DWord -Force | Out-Null
