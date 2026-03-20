#!/usr/bin/env pwsh
# One-line install: irm https://raw.githubusercontent.com/JoshLove-msft/upgrade-pr-agent/master/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$repo = "JoshLove-msft/upgrade-pr-agent"
$tool = "UpgradePrAgent"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "upa-install-$(Get-Random)"

Write-Host "Installing upgrade-pr-agent..." -ForegroundColor Cyan

try {
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    # Get latest release asset URL
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
    $asset = $release.assets | Where-Object { $_.name -like "*.nupkg" } | Select-Object -First 1

    if (-not $asset) {
        Write-Error "No .nupkg found in latest release"
        return
    }

    $nupkgPath = Join-Path $tmp $asset.name
    Write-Host "  Downloading $($asset.name)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $nupkgPath

    # Create isolated nuget.config to avoid feed auth issues
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$tmp" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $tmp "nuget.config")

    # Uninstall previous version if present
    dotnet tool uninstall -g $tool 2>$null

    # Install
    dotnet tool install -g $tool --configfile (Join-Path $tmp "nuget.config")

    Write-Host ""
    Write-Host "Done! Run 'upgrade-pr-agent --help' to get started." -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
