[CmdletBinding()]
param()
# Builds installer\dist\VPS-Tunnel-Setup.exe from the source code in this repository and the
# sing-box engine in C:\sing-box\sing-box-1.14.1-windows-amd64. No connection settings are included.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $PSScriptRoot 'stage'
$output = Join-Path $PSScriptRoot 'dist'
$engine = 'C:\sing-box\sing-box-1.14.1-windows-amd64\sing-box.exe'
if (-not (Test-Path -LiteralPath $engine)) { throw "sing-box 1.14.1 not found: $engine" }

foreach ($dir in $stage, $output) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
dotnet publish (Join-Path $root 'VPS.Tunnel.App\VPS.Tunnel.App.csproj') -c Release -o (Join-Path $stage 'gui')
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
dotnet publish (Join-Path $root 'VPS.Tunnel.Service\VPS.Tunnel.Service.csproj') -c Release -o (Join-Path $stage 'service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'VPS.Tunnel.Deployment.Setup\VPS.Tunnel.Deployment.Setup.csproj') -c Release -o $output
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }

$setup = Join-Path $output 'VPS-Tunnel-Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw 'Setup EXE was not produced.' }
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host ''
Write-Host "Done: $setup" -ForegroundColor Green
