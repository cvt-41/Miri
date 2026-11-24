# Builds dist\Miri-Setup.zip: self-contained publish + installer scripts. Run from anywhere.
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot
$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
Remove-Item (Join-Path $dist 'Miri'), $stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\Miri.App') -c Release -r win-x64 --self-contained -o (Join-Path $dist 'Miri')
if ($LASTEXITCODE) { throw 'publish failed' }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $dist 'Miri') $stage -Recurse
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'Miri')
Copy-Item (Join-Path $root 'PRIVACY.md') (Join-Path $stage 'PRIVACY.txt')
Get-ChildItem $PSScriptRoot -File | Where-Object Name -ne 'build.ps1' | Copy-Item -Destination $stage
$zip = Join-Path $dist 'Miri-Setup.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive (Join-Path $stage '*') $zip
Remove-Item $stage -Recurse -Force
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
