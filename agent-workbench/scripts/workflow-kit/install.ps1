# Restores the exported Agent Workbench setup on a Windows PC: WSL part first
# (install.sh), then font, Windows Terminal profile and the Explorer context
# menu "Open in Claude Code". Safe to run again.

$ErrorActionPreference = "Continue"
$Bundle = $PSScriptRoot

function Say($m)  { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Skip($m) { Write-Host "  [skip] $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }

# --- WSL -------------------------------------------------------------------
Say "WSL"
$distros = @()
try {
    $distros = (wsl.exe -l -q 2>$null) -replace "`0", "" | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_.Trim() }
} catch { }
if (-not $distros) {
    Write-Host "  No WSL distribution found. Installing Ubuntu now."
    wsl.exe --install -d Ubuntu
    Write-Host ""
    Write-Host "  Restart Windows if asked, open 'Ubuntu' once from the start menu to"
    Write-Host "  create your Linux user, then run install.bat again."
    exit 0
}
$distro = $distros | Where-Object { $_ -like "Ubuntu*" } | Select-Object -First 1
if (-not $distro) { $distro = $distros[0] }
Ok "distribution: $distro"

$wslBundle = (wsl.exe -d $distro -e wslpath -a "$Bundle").Trim()
$wslHome   = (wsl.exe -d $distro -e sh -c 'printf %s "$HOME"').Trim()

Say "Linux part (install.sh)"
wsl.exe -d $distro -e bash "$wslBundle/install.sh"
if ($LASTEXITCODE -ne 0) {
    Warn "install.sh stopped with exit code $LASTEXITCODE, fix the cause and run install.bat again"
    exit 1
}

# --- Nerd Font ------------------------------------------------------------
Say "Nerd Font (MesloLGSDZ)"
$fontKeys = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"
$hasFont = $fontKeys | ForEach-Object { Get-ItemProperty $_ -ErrorAction SilentlyContinue } |
    ForEach-Object { $_.PSObject.Properties.Name } | Where-Object { $_ -match "MesloLGSDZ" }
if ($hasFont) {
    Skip "already installed"
} else {
    try {
        $zip = Join-Path $env:TEMP "Meslo.zip"
        $dir = Join-Path $env:TEMP "Meslo"
        Invoke-WebRequest "https://github.com/ryanoasis/nerd-fonts/releases/latest/download/Meslo.zip" -OutFile $zip -UseBasicParsing
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        Expand-Archive $zip -DestinationPath $dir
        $fonts = (New-Object -ComObject Shell.Application).Namespace(0x14)
        Get-ChildItem "$dir\MesloLGSDZNerdFont*.ttf" | ForEach-Object { $fonts.CopyHere($_.FullName, 0x14) }
        Ok "installed"
    } catch {
        Warn "font install failed: $($_.Exception.Message)"
    } finally {
        Remove-Item $zip, $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- Launcher and context menu --------------------------------------------
Say "Explorer context menu 'Open in Claude Code'"
$appDir = Join-Path $env:APPDATA "ClaudeCode"
New-Item -ItemType Directory -Force $appDir | Out-Null
$ico = Join-Path $appDir "claude-code.ico"
Copy-Item "$Bundle\windows\claude-code.ico" $ico -Force
$launcher = Get-Content "$Bundle\windows\ClaudeHere.ps1" -Raw
$launcher = $launcher -replace '\$distro\s*=\s*"[^"]*"', "`$`$distro  = `"$distro`""
$launcher = $launcher -replace '\$wslHome\s*=\s*"[^"]*"', "`$`$wslHome = `"$wslHome`""
Set-Content (Join-Path $appDir "ClaudeHere.ps1") $launcher -Encoding UTF8
$cmd = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$appDir\ClaudeHere.ps1`" `"%V`""
foreach ($base in "HKCU:\Software\Classes\Directory\shell\ClaudeCode", "HKCU:\Software\Classes\Directory\Background\shell\ClaudeCode") {
    New-Item -Force "$base\command" | Out-Null
    Set-ItemProperty $base -Name "(default)" -Value "Open in Claude Code"
    Set-ItemProperty $base -Name "Icon" -Value $ico
    Set-ItemProperty "$base\command" -Name "(default)" -Value $cmd
}
Ok "folder and folder background"

# --- Windows Terminal profile ---------------------------------------------
Say "Windows Terminal profile 'Claude Code'"
$wtSettings = Get-ChildItem "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal*\LocalState\settings.json" -ErrorAction SilentlyContinue | Select-Object -First 1
$fragmentDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows Terminal\Fragments\ClaudeWorkflow"
if ($wtSettings -and (Select-String -Path $wtSettings.FullName -Pattern '"name":\s*"Claude Code"' -Quiet)) {
    Skip "profile already in settings.json"
} elseif (-not (Test-Path "$Bundle\windows\terminal-fragment.json")) {
    Warn "no profile in the package"
} else {
    $fragment = Get-Content "$Bundle\windows\terminal-fragment.json" -Raw | ConvertFrom-Json
    foreach ($p in $fragment.profiles) {
        $p.commandline = "wsl.exe -d $distro -e bash -lc $wslHome/claude-tmux.sh"
        $p.icon = $ico -replace '\\', '/'
    }
    New-Item -ItemType Directory -Force $fragmentDir | Out-Null
    $fragment | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $fragmentDir "claude.json") -Encoding UTF8
    Ok "added as fragment, appears after restarting Windows Terminal"
}

# --- Obsidian ---------------------------------------------------------------
Say "Obsidian"
$obsidian = Test-Path "$env:LOCALAPPDATA\Programs\Obsidian\Obsidian.exe"
if ($obsidian) {
    Skip "already installed"
} elseif (Get-Command winget -ErrorAction SilentlyContinue) {
    if ((Read-Host "  Install Obsidian with winget? [Y/n]") -ne "n") {
        winget install --id Obsidian.Obsidian -e --accept-package-agreements --accept-source-agreements
    }
} else {
    Warn "winget missing, install Obsidian from https://obsidian.md"
}

Say "Left to do by hand"
Write-Host "  1. Windows Terminal: profile 'Claude Code' or right click a folder > 'Open in Claude Code'"
Write-Host "  2. At the first start of claude: log in"
Write-Host "  3. Chrome: install the 'Claude in Chrome' extension and log in"
Write-Host "  4. Obsidian: 'Open folder as vault' > Documents\claude-vault, then trust its plugins"
Write-Host "  5. Project checkouts, Docker and SSH keys are not part of this package"
