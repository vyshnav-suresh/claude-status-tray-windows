# Publish the single-file, self-contained tray .exe (no .NET runtime needed on the target).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'

dotnet publish (Join-Path $root 'app\ClaudeStatusTray.csproj') `
    -c Release -r win-x64 `
    -p:PublishSingleFile=true -p:SelfContained=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

$exe = Join-Path $out 'ClaudeStatusTray.exe'
Write-Host "Built: $exe"

# Auto-sign if a cert is configured (else ship unsigned). EV token: set CSC_THUMBPRINT.
# OV PFX: set CSC_PFX (path) and CSC_PFX_PASSWORD.
if ($env:CSC_THUMBPRINT) {
    & (Join-Path $root 'sign.ps1') -File $exe -Thumbprint $env:CSC_THUMBPRINT
} elseif ($env:CSC_PFX -and $env:CSC_PFX_PASSWORD) {
    $sec = ConvertTo-SecureString $env:CSC_PFX_PASSWORD -AsPlainText -Force
    & (Join-Path $root 'sign.ps1') -File $exe -PfxPath $env:CSC_PFX -PfxPassword $sec
} else {
    Write-Host "Unsigned (no CSC_THUMBPRINT / CSC_PFX set). SmartScreen will warn on other machines."
}
