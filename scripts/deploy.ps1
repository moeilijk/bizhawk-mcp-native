# Builds the external tool (Release) and drops it into the install's
# ExternalTools folder. Run from PowerShell on Windows:
#   .\scripts\deploy.ps1
# Optional: -BizHawkInstall "F:\path\to\BizHawk" (or set BIZHAWK_INSTALL in
# .env — see .env.example; the script reads .env automatically).
param(
    [string]$BizHawkInstall = ""
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# read .env if present (KEY=VALUE lines; KEY only for quoted values)
$envFile = Join-Path (Get-Location) ".env"
if (Test-Path $envFile) {
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], "Process")
        }
    }
}
if (-not $BizHawkInstall) { $BizHawkInstall = $env:BIZHAWK_INSTALL }

$args = @("build", "src/BizHawkMcp/BizHawkMcp.csproj", "-c", "Release")
if ($BizHawkInstall) { $args += "-p:BizHawkInstallDir=$BizHawkInstall" }

& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. In EmuHawk: Tools > External Tools > 'BizHawk MCP Server'"
