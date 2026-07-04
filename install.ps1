# Install ClaudeStatusTray: build the exe, drop it in LocalAppData, wire the Claude Code
# hooks, and launch it (the app self-registers autostart on first run). Re-runnable.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "Node.js is required (the hooks run under it). Get it from https://nodejs.org and re-run."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 9 SDK is required to build the app. Install it with:`n" +
          "  winget install Microsoft.DotNet.SDK.9`n" +
          "...then re-run. (Or download the prebuilt release zip instead - see the README.)"
}

# 1. Build the exe if it isn't there yet.
$exe = Join-Path $root 'dist\ClaudeStatusTray.exe'
if (-not (Test-Path $exe)) { & (Join-Path $root 'build.ps1') }

# 2. Copy into a stable location (dist\ is a build dir that may be wiped).
$dest = Join-Path $env:LOCALAPPDATA 'ClaudeStatusTray'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$installed = Join-Path $dest 'ClaudeStatusTray.exe'
Get-Process ClaudeStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500   # let the file handle release before we overwrite
Copy-Item $exe $installed -Force

# 3. Wire the Claude Code hooks (merges into ~/.claude/settings.json, backs it up, quotes paths).
node (Join-Path $root 'hooks\install.js')

# 4. Launch — this registers the HKCU Run autostart pointing at the installed exe.
Start-Process $installed
Write-Host "Installed to $installed and launched. Hooks wired; autostart registered."
