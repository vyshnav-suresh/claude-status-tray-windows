# Remove ClaudeStatusTray: unwire hooks, stop the app, drop autostart, delete the install dir.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

# 1. Strip the hooks from ~/.claude/settings.json (leaves other hooks intact).
if (Get-Command node -ErrorAction SilentlyContinue) {
    node (Join-Path $root 'hooks\uninstall.js')
}

# 2. Stop the running app.
Get-Process ClaudeStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. Remove autostart.
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'ClaudeStatusTray' -ErrorAction SilentlyContinue

# 4. Delete the install dir.
$dest = Join-Path $env:LOCALAPPDATA 'ClaudeStatusTray'
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }

Write-Host "Uninstalled. (State files under ~\.claude\statusbar were left; delete manually if desired.)"
