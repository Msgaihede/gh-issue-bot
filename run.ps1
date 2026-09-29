<#
.SYNOPSIS
Runs the bot — or a one-shot mode such as --dry-run — with the settings in .env.

.DESCRIPTION
The bot never reads .env itself (only docker compose does), so this script loads it into its own process
environment first. Lines are KEY=VALUE; blank lines, comments and keys without a value are skipped, so an
unfilled line never overrides a value set elsewhere. Bot settings saved as Windows user variables after
this terminal was opened are picked up too. A value in .env wins over a Windows variable of the same name.

.EXAMPLE
./run.ps1

.EXAMPLE
./run.ps1 --dry-run owner/repo "The save button does nothing after I rotate the phone"
#>
$ErrorActionPreference = 'Stop'

$envFile = Join-Path $PSScriptRoot '.env'
if (-not (Test-Path $envFile)) {
    Write-Error "No .env next to run.ps1 — copy .env.example to .env and fill it in."
}

foreach ($line in Get-Content $envFile) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }

    $eq = $trimmed.IndexOf('=')
    if ($eq -lt 1) { continue }

    $name = $trimmed.Substring(0, $eq).Trim()
    $value = $trimmed.Substring($eq + 1).Trim()
    if ($value.Length -ge 2 -and $value[0] -eq $value[-1] -and $value[0] -in '"', "'") {
        $value = $value.Substring(1, $value.Length - 2)
    }
    if ($value -eq '') { continue }

    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}

# A terminal opened before a Windows user variable was saved does not have it yet.
$userVariables = [Environment]::GetEnvironmentVariables('User')
foreach ($name in $userVariables.Keys) {
    if ($name -match '^(Discord|OpenRouter|Apps|Limits|Database)__' -and
        -not [Environment]::GetEnvironmentVariable($name, 'Process')) {
        [Environment]::SetEnvironmentVariable($name, [string]$userVariables[$name], 'Process')
    }
}

# The content root is where appsettings.json lives; without it, `dotnet run` from the repository root runs
# on built-in defaults only (and logs every SQL statement). The database still lands in ./db, which is
# gitignored.
$env:DOTNET_CONTENTROOT = Join-Path $PSScriptRoot 'src/DiscordGithubBot'

Push-Location $PSScriptRoot
try {
    dotnet run --project (Join-Path $PSScriptRoot 'src/DiscordGithubBot') -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
