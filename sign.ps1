# Authenticode-sign a file with signtool. Supply EITHER a PFX (OV certs) or a cert-store
# thumbprint (EV certs live on a hardware token in the Windows cert store).
#
#   .\sign.ps1 -PfxPath cert.pfx -PfxPassword (Read-Host -AsSecureString)
#   .\sign.ps1 -Thumbprint AB12...            # EV token / cert already in CurrentUser\My
#
# Signing does not by itself clear SmartScreen: an OV signature must accrue download
# reputation over time; only an EV certificate grants immediate trust.
param(
    [string]$File = (Join-Path $PSScriptRoot 'dist\ClaudeStatusTray.exe'),
    [string]$PfxPath,
    [System.Security.SecureString]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) { throw "File not found: $File (run build.ps1 first)" }
if (-not $PfxPath -and -not $Thumbprint) { throw "Provide -PfxPath or -Thumbprint." }

# Newest signtool from the Windows SDK (prefer x64).
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object { $_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found. Install the Windows 10/11 SDK." }

# RFC3161 timestamp so the signature stays valid after the cert expires.
$args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
if ($Thumbprint) {
    $args += @('/sha1', $Thumbprint)
} else {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $pw = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
    $args += @('/f', $PfxPath, '/p', $pw)
}
$args += $File

& $signtool.FullName @args
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)" }

& $signtool.FullName verify /pa /v $File
if ($LASTEXITCODE -ne 0) { throw "signtool verify failed ($LASTEXITCODE)" }
Write-Host "Signed and verified: $File"
