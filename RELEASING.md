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

## Code signing (optional)

`build.ps1` signs the exe automatically **if** a certificate is configured via env vars,
otherwise it ships unsigned:

```powershell
# EV certificate (hardware token / cert store) — grants immediate SmartScreen trust
$env:CSC_THUMBPRINT = 'AB12CD...'          # thumbprint from certmgr / the token
.\build.ps1

# OV certificate (.pfx file) — signs, but SmartScreen clears only after reputation accrues
$env:CSC_PFX = 'C:\path\cert.pfx'
$env:CSC_PFX_PASSWORD = '...'
.\build.ps1
```

Or sign an already-built exe directly with `.\sign.ps1` (see its header for args). Signing
uses SHA-256 with an RFC3161 timestamp so the signature survives cert expiry.

**Reality check:** signing does not by itself remove the SmartScreen "unknown publisher"
prompt. An **OV** signature must build download reputation over time; only an **EV**
certificate grants immediate trust. Both require buying a cert from a CA (DigiCert, Sectigo,
…) and passing identity verification. There is no free path.

---

**Notes**
- Trimming is disabled (WinForms + reflection); compression is the only size lever, already
  on in `build.ps1`.
