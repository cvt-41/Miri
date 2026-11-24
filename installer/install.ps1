# Installs Miri for the current user (no admin needed) and sets up Ollama + the models she uses.
$ErrorActionPreference = 'Stop'
$src  = Join-Path $PSScriptRoot 'Miri'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Miri'
$exe  = Join-Path $dest 'Miri.App.exe'
if (-not (Test-Path (Join-Path $src 'Miri.App.exe'))) { throw "Miri files not found next to install.ps1. Extract the whole zip first." }

Write-Host @"

Miri - free, open-source desktop kitten (MIT License).
When you ask, Miri clicks and types in your apps for you. She asks before risky
steps, but she can make mistakes: check her work. Miri is provided "AS IS",
without warranty of any kind (see LICENSE). Privacy: everything stays on this PC
except weather lookups (see PRIVACY.md).

"@
if ((Read-Host "Continue and install? (y/n)") -ne 'y') { Write-Host "Cancelled."; exit 1 }
Write-Host "Installing Miri to $dest ..."
Get-Process Miri.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $src $dest -Recurse
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dest
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $dest
Get-ChildItem $dest -Recurse -File | Unblock-File   # drop the "downloaded from the internet" mark

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path, $target, $arguments = '') {
    $s = $shell.CreateShortcut($path); $s.TargetPath = $target; $s.Arguments = $arguments
    $s.WorkingDirectory = $dest; $s.IconLocation = $exe; $s.Save()
}
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Miri'
New-Item -ItemType Directory -Force $menu | Out-Null
New-Shortcut (Join-Path $menu 'Miri.lnk') $exe
New-Shortcut (Join-Path $menu 'Uninstall Miri.lnk') 'powershell.exe' "-NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Miri.lnk') $exe

# Settings > Apps entry
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Miri'
New-Item $key -Force | Out-Null
Set-ItemProperty $key DisplayName 'Miri'
Set-ItemProperty $key Publisher 'Miri'
Set-ItemProperty $key DisplayIcon $exe
Set-ItemProperty $key InstallLocation $dest
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord

# ---- Ollama (Miri's brain runs locally through it)
$ollama = (Get-Command ollama -ErrorAction SilentlyContinue).Source
if (-not $ollama) { $p = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'; if (Test-Path $p) { $ollama = $p } }
if (-not $ollama) {
    Write-Host "`nMiri needs Ollama to think. It is not installed."
    if ((Read-Host "Install Ollama now? (y/n)") -eq 'y') {
        if (Get-Command winget -ErrorAction SilentlyContinue) { winget install --id Ollama.Ollama -e }
        else {
            # no winget (older or stripped-down Windows): official installer from ollama.com, shown to the user as normal
            Write-Host "Downloading the Ollama installer from ollama.com (about 2 GB)..."
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $setup = Join-Path $env:TEMP 'OllamaSetup.exe'
            try {
                $ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest very slow on PowerShell 5.1
                Invoke-WebRequest 'https://ollama.com/download/OllamaSetup.exe' -OutFile $setup -UseBasicParsing
                Start-Process $setup -Wait
            } catch { Write-Host "Download failed: $($_.Exception.Message)" }
            finally { Remove-Item $setup -ErrorAction SilentlyContinue }
        }
        $p = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'; if (Test-Path $p) { $ollama = $p }
    }
    if (-not $ollama) { Write-Host "Get Ollama from https://ollama.com/download, then run this installer again to fetch the models." }
}
if ($ollama) {
    $models = 'qwen3.5:4b', 'gemma4:e4b'   # chat, vision (must match settings.json)
    Write-Host "`nMiri uses these models: $($models -join ', ') (several GB to download)."
    Write-Host "Or answer n: on first start Miri suggests a chat + vision model pair that fits this PC."
    Write-Host "By downloading them you agree to their licenses (Qwen license, Gemma Terms of Use): https://ollama.com/library"
    if ((Read-Host "Download them now? (y/n)") -eq 'y') {
        if (-not (Get-Process ollama -ErrorAction SilentlyContinue)) {
            $app = Join-Path (Split-Path $ollama) 'ollama app.exe'
            if (Test-Path $app) { Start-Process $app } else { Start-Process $ollama serve -WindowStyle Hidden }
            Start-Sleep 5
        }
        foreach ($m in $models) { & $ollama pull $m }
    }
}

Write-Host "`nDone. Starting Miri..."
Start-Process $exe
