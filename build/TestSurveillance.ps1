$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & "$PSScriptRoot/Build.ps1" -Version 1.7
    $sdk = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
    & $sdk run --project scripts/SurveillanceRegression -c Release -p:RimWorldVersion=1.7 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Surveillance Shift regression failed.' }
} finally { Pop-Location }
