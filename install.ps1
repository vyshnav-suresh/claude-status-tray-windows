# Install ClaudeStatusTray: build the exe, drop it in LocalAppData, wire the Claude Code
# hooks, and launch it (the app self-registers autostart on first run). Re-runnable.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "Node.js is required (the hooks run under it). Install Node and re-run."
}

# 1. Build the exe if it isn't there yet.
$exe = Join-Path $root 'dist\ClaudeStatusTray.exe'
if (-not (Test-Path $exe)) { & (Join-Path $root 'build.ps1') }

# 2. Copy into a stable location (dist\ is a build dir that may be wiped).
$dest = Join-Path $env:LOCALAPPDATA 'ClaudeStatusTray'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$installed = Join-Path $dest 'ClaudeStatusTray.exe'
Get-Process ClaudeStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item $exe $installed -Force

# 3. Wire the Claude Code hooks (merges into ~/.claude/settings.json, backs it up, quotes paths).
node (Join-Path $root 'hooks\install.js')

# 4. Launch — this registers the HKCU Run autostart pointing at the installed exe.
Start-Process $installed
Write-Host "Installed to $installed and launched. Hooks wired; autostart registered."
