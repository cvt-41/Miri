# Builds dist\Miri-Setup.exe (one-file installer) and dist\Miri-Setup.zip: self-contained publish + installer scripts. Run from anywhere.
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
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

# ---- Miri-Setup.exe with Windows' built-in IExpress. It packs flat files only, so Miri's folder goes in as one zip
# and setup.cmd unpacks it next to install.ps1, then runs the installer in a console (it asks y/n questions).
$pack = Join-Path $dist 'pack'
Remove-Item $pack -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pack | Out-Null
Compress-Archive (Join-Path $stage 'Miri') (Join-Path $pack 'Miri.zip')
Get-ChildItem $stage -File | Copy-Item -Destination $pack
Set-Content (Join-Path $pack 'setup.cmd') -Encoding ascii -Value @'
@echo off
title Miri setup
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Force '%~dp0Miri.zip' '%~dp0'"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if not defined MIRI_UPDATE pause
'@
$exe = Join-Path $dist 'Miri-Setup.exe'
Remove-Item $exe -ErrorAction SilentlyContinue
$files = Get-ChildItem $pack -File
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$exe
FriendlyName=Miri setup
AppLaunched=cmd /c setup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
$(($files | ForEach-Object -Begin { $i = 0 } -Process { "FILE$i=`"$($_.Name)`""; $i++ }) -join "`r`n")
[SourceFiles]
SourceFiles0=$pack\
[SourceFiles0]
$(($files | ForEach-Object -Begin { $i = 0 } -Process { "%FILE$i%="; $i++ }) -join "`r`n")
"@
$sedPath = Join-Path $dist 'miri.sed'
Set-Content $sedPath $sed -Encoding ascii
Start-Process iexpress.exe -ArgumentList '/N', '/Q', $sedPath -Wait -NoNewWindow
Remove-Item $stage, $pack, $sedPath -Recurse -Force
if (-not (Test-Path $exe)) { throw 'IExpress did not produce Miri-Setup.exe' }
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)"
