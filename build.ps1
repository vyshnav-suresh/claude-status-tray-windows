# Publish the single-file, self-contained tray .exe (no .NET runtime needed on the target).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'

dotnet publish (Join-Path $root 'app\ClaudeStatusTray.csproj') `
    -c Release -r win-x64 `
    -p:PublishSingleFile=true -p:SelfContained=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

Write-Host "Built: $(Join-Path $out 'ClaudeStatusTray.exe')"
