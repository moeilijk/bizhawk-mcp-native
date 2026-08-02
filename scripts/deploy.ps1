# Builds the external tool (Release) and drops it into the install's
# ExternalTools folder. Run from PowerShell on Windows:
#   .\scripts\deploy.ps1
# Optional: -BizHawkInstall "F:\path\to\BizHawk"
param(
    [string]$BizHawkInstall = ""
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$args = @("build", "src/BizHawkMcp/BizHawkMcp.csproj", "-c", "Release")
if ($BizHawkInstall) { $args += "-p:BizHawkInstallDir=$BizHawkInstall" }

& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. In EmuHawk: Tools > External Tools > 'BizHawk MCP Server'"
