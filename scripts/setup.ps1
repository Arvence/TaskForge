#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'
$templatePath = Join-Path $repoRoot '.env.example'
$composeArgs = @('compose', '--project-directory', $repoRoot, '--env-file', $envPath, '-f', (Join-Path $repoRoot 'compose.yaml'))

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    $ErrorActionPreference = 'Continue'
    $output = & docker @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
    return ($output -join "`n")
}

function Read-Password([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secure.Dispose()
    }
}

Push-Location $repoRoot
try {
    Write-Host '## TaskForge Setup'
    Write-Host ''
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker was not found. Install Docker with Compose v2, start it, then run ./setup.ps1 again.'
    }

    $null = Invoke-Docker @('compose', 'version') 'Docker Compose v2 is unavailable. Install or update Docker Compose, then rerun setup.'
    $osType = Invoke-Docker @('info', '--format', '{{.OSType}}') 'Docker is not running or cannot be accessed. Start Docker Desktop (or Docker Engine), then rerun setup.'
    if ($osType.Trim() -ne 'linux') {
        throw 'TaskForge requires Linux containers. Switch Docker Desktop to Linux containers, then rerun setup.'
    }
    Write-Host '[OK] Docker is running with Linux containers.'

    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
        throw '.env.example is missing. Restore it from the repository, then rerun setup.'
    }
    if (Test-Path Env:MSSQL_SA_PASSWORD) {
        throw 'MSSQL_SA_PASSWORD is set in this terminal and would override .env. Open a terminal without that override, then rerun setup.'
    }

    $template = [IO.File]::ReadAllText($templatePath)
    $passwordPattern = '(?m)^(?<prefix>[ \t]*(?:export[ \t]+)?MSSQL_SA_PASSWORD[ \t]*=[ \t]*)(?<value>[^\r\n]*)'
    $templateEntries = [regex]::Matches($template, $passwordPattern)
    if ($templateEntries.Count -ne 1) {
        throw '.env.example must contain one MSSQL_SA_PASSWORD assignment.'
    }
    $placeholder = $templateEntries[0].Groups['value'].Value.Trim()

    if (-not (Test-Path -LiteralPath $envPath)) {
        Copy-Item -LiteralPath $templatePath -Destination $envPath
        Write-Host '[OK] Created .env from .env.example.'
    }
    else {
        Write-Host '[OK] Keeping existing .env.'
    }

    $content = [IO.File]::ReadAllText($envPath)
    $entries = [regex]::Matches($content, $passwordPattern)
    if ($entries.Count -gt 1) {
        throw '.env contains multiple MSSQL_SA_PASSWORD assignments. Keep the correct existing password in a single assignment, then rerun setup.'
    }

    $unsetPattern = '^\s*(?:' + [regex]::Escape($placeholder) + '|''(?:' + [regex]::Escape($placeholder) + ')?''|"(?:' + [regex]::Escape($placeholder) + ')?")?\s*(?:#.*)?$'
    if ($entries.Count -eq 0 -or $entries[0].Groups['value'].Value -match $unsetPattern) {
        Write-Host 'Use 8-128 characters and at least three of: uppercase, lowercase, digits, symbols.'
        Write-Host 'Spaces, quotes, semicolons, and backslashes are not supported by this setup flow.'
        Write-Host 'If a SQL Server data volume already exists, enter its existing SA password.'
        do {
            $password = Read-Password 'SQL Server password'
            $categoryCount = 0
            foreach ($pattern in @('[A-Z]', '[a-z]', '[0-9]', '[^A-Za-z0-9]')) {
                if ($password -cmatch $pattern) { $categoryCount++ }
            }
            if ($password.Length -lt 8 -or $password.Length -gt 128 -or $categoryCount -lt 3 -or $password -match '[\s\p{C};''"\\]' -or $password -ceq $placeholder) {
                Write-Host '[ERROR] Choose a different password that meets the rules above.'
                continue
            }
            $confirmation = Read-Password 'Confirm SQL Server password'
            if ($password -cne $confirmation) {
                Write-Host '[ERROR] Passwords do not match. Try again.'
                continue
            }
            break
        } while ($true)

        $assignment = "MSSQL_SA_PASSWORD='$password'"
        if ($entries.Count -eq 1) {
            $entry = $entries[0]
            $comment = [regex]::Match($entry.Groups['value'].Value, '[ \t]*#.*$').Value
            if ($comment -and $comment -notmatch '^[ \t]') { $comment = ' ' + $comment }
            $replacement = $entry.Groups['prefix'].Value + "'$password'" + $comment
            $content = $content.Substring(0, $entry.Index) + $replacement + $content.Substring($entry.Index + $entry.Length)
        }
        else {
            $newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
            if ($content.Length -gt 0 -and -not $content.EndsWith("`n")) { $content += $newline }
            $content += $assignment + $newline
        }
        [IO.File]::WriteAllText($envPath, $content, [Text.UTF8Encoding]::new($false))
        $password = $confirmation = $content = $assignment = $replacement = $null
        Write-Host '[OK] Configuration saved.'
    }
    else {
        Write-Host '[OK] Existing SQL Server password preserved. Setup does not rotate database passwords.'
    }

    $configJson = Invoke-Docker ($composeArgs + @('config', '--format', 'json')) 'Compose configuration is invalid. Check compose.yaml and .env locally; do not share resolved configuration because it contains secrets.'
    try {
        $config = $configJson | ConvertFrom-Json
    }
    catch {
        throw 'Could not read the resolved Compose configuration. Update Docker Compose and check compose.yaml locally.'
    }
    $configuredPassword = $config.services.sqlserver.environment.MSSQL_SA_PASSWORD
    if ([string]::IsNullOrWhiteSpace($configuredPassword) -or $configuredPassword -ceq $placeholder) {
        throw 'MSSQL_SA_PASSWORD resolves to an empty or placeholder value. Correct the existing .env without changing an established database password.'
    }
    $port = @($config.services.api.ports | Where-Object { $_.target -eq 8080 })[0].published
    if (-not $port) { throw 'Compose does not publish the API container port 8080.' }
    $baseUrl = "http://localhost:$port"
    $configJson = $config = $configuredPassword = $null
    Write-Host '[OK] Compose configuration validated.'
    Write-Host "API: $baseUrl"
    Write-Host 'Workers: default 1; existing database settings are preserved. Change the count through /api/workers/count.'

    do {
        $answer = (Read-Host 'Start TaskForge now? [Y/n]').Trim()
    } while ($answer -notmatch '^(|y|yes|n|no)$')
    if ($answer -match '^(n|no)$') {
        Write-Host 'Setup complete. Start later with: docker compose up --build -d'
        return
    }

    Write-Host 'Building and starting TaskForge. The first run may take several minutes...'
    $null = Invoke-Docker ($composeArgs + @('up', '--build', '-d', '--wait', '--wait-timeout', '180')) 'Docker Compose could not build or start TaskForge. For build failures, rerun docker compose up --build -d to see the build output.'
    Write-Host 'Waiting for application readiness...'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    do {
        try {
            $response = Invoke-WebRequest "$baseUrl/api/ready" -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200 -and ($response.Content | ConvertFrom-Json).status -eq 'Ready') {
                Write-Host ''
                Write-Host 'TaskForge is ready.'
                Write-Host "API: $baseUrl"
                Write-Host "Ready: $baseUrl/api/ready"
                Write-Host "Health: $baseUrl/api/health"
                Write-Host "OpenAPI: $baseUrl/openapi/v1.json"
                return
            }
        }
        catch { }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'TaskForge did not become ready within 120 seconds after container startup.'
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host 'Inspect service logs from the repository root: docker compose logs --tail 100 api sqlserver'
    exit 1
}
finally {
    Pop-Location
}
