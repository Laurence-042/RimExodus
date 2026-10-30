$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & "$PSScriptRoot/Build.ps1" -Version 1.7
    $sdk = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
    & $sdk build scripts/CompatRegression -c Release -p:RimWorldVersion=1.7 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility regression build failed.' }
    foreach ($mode in @('legacy', 'ref-backend', 'preview-drift')) {
        & $sdk run --no-build --project scripts/CompatRegression -c Release -p:RimWorldVersion=1.7 -- $mode
        if ($LASTEXITCODE -ne 0) { throw "Compatibility regression failed: $mode" }
    }
} finally { Pop-Location }
