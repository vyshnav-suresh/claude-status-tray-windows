# Releasing

How to cut a new version. Example below cuts `v0.1.2` — substitute your version.

## 1. Bump the version

Edit `app/ClaudeStatusTray.csproj`:

```xml
<Version>0.1.2</Version>
```

This must match the git tag (minus the `v`). The in-app update check compares the
assembly version against the latest GitHub release tag, so a mismatch means users
get notified even when they're current.

## 2. Build the exe

```powershell
.\build.ps1          # -> dist\ClaudeStatusTray.exe (single-file, self-contained, compressed)
```

Sanity check it runs and reports the new version:

```powershell
.\dist\ClaudeStatusTray.exe --selftest                    # -> selftest OK
(Get-Item .\dist\ClaudeStatusTray.exe).VersionInfo.ProductVersion
```

## 3. Package the release zip

The zip is a drop-in installer: the exe + hooks + a build-free `install.ps1`. Refresh
the staged exe/README, then zip:

```powershell
$stage = 'dist\claude-status-tray-windows'
Copy-Item .\dist\ClaudeStatusTray.exe $stage -Force
Copy-Item .\README.md $stage -Force
$zip = 'dist\claude-status-tray-windows-v0.1.2.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $true)
```

The `dist\claude-status-tray-windows\` staging folder holds the release layout
(exe, `install.ps1`, `uninstall.ps1`, `README.md`, `hooks\`). Its `install.ps1` is the
build-free one — it uses the bundled exe rather than compiling. If you ever need to
rebuild that folder from scratch, see the layout in the last release's zip.

> `dist/` is gitignored — the zip is a build artifact, not committed. It lives only on
> the GitHub Release.

## 4. Commit and push the bump

```powershell
git add app/ClaudeStatusTray.csproj
git commit -m "Bump version to 0.1.2"
git push origin main
```

## 5. Cut the GitHub release

Tags the current `main` and uploads the zip:

```powershell
gh release create v0.1.2 `
  "dist/claude-status-tray-windows-v0.1.2.zip" `
  --repo vyshnav-suresh/claude-status-tray-windows `
  --title "v0.1.2" `
  --notes "What changed..."
```

## 6. (Optional) refresh your own install

So your running copy isn't flagged as outdated:

```powershell
Get-Process ClaudeStatusTray -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item .\dist\ClaudeStatusTray.exe "$env:LOCALAPPDATA\ClaudeStatusTray\ClaudeStatusTray.exe" -Force
Start-Process "$env:LOCALAPPDATA\ClaudeStatusTray\ClaudeStatusTray.exe"
```

---

---

**Notes**
- The exe is unsigned, so users get a SmartScreen "unknown publisher" prompt on first run
  (More info → Run anyway) and may need `-ExecutionPolicy Bypass` to run `install.ps1`.
  Removing that requires a paid code-signing certificate — not set up.
- Trimming is disabled (WinForms + reflection); compression is the only size lever, already
  on in `build.ps1`.
