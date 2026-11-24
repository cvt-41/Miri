# Removes Miri (leaves Ollama, its models and Miri's settings in %APPDATA% alone).
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Miri'
Get-Process Miri.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Miri') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Miri.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Miri' -Force -ErrorAction SilentlyContinue
Set-Location $env:TEMP
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Miri has been removed."
