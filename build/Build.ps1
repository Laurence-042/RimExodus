param([ValidateSet('1.6','1.7')][string]$Version='1.7', [ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$framework = [IO.Path]::GetFullPath((Join-Path $root '../RimExodusFramework'))
if (!(Test-Path -LiteralPath "$framework/justfile")) { throw "Sibling REF project missing: $framework" }
$localSdk = Join-Path $root '../RimExodus_Delta-V/.local-tools/dotnet/dotnet.exe'
if (!$env:DOTNET_EXE -and $Version -eq '1.7' -and (Test-Path -LiteralPath $localSdk)) {
    $env:DOTNET_EXE = [IO.Path]::GetFullPath($localSdk)
}
$sdk = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
Push-Location $root
try {
    & just --justfile "$framework/justfile" build $Version
    if ($LASTEXITCODE -ne 0) { throw 'REF build failed; RimExodus was not built.' }
    $reference = Join-Path $root "References/REF/$Version"
    New-Item -ItemType Directory -Force -Path $reference | Out-Null
    Copy-Item -LiteralPath "$framework/$Version/Assemblies/RimExodus.Maps.dll" -Destination $reference -Force
    # Runtime ownership is exclusively REF, matching the Delta-V build contract.
    foreach ($name in @('RimExodus.Maps.dll', 'RimExodus.Maps.deps.json')) {
        $embedded = Join-Path $root "$Version/Assemblies/$name"
        if (Test-Path -LiteralPath $embedded) { Remove-Item -LiteralPath $embedded -Force }
    }
    & $sdk build Source/RimExodus.csproj -c $Configuration "-p:RimWorldVersion=$Version" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "RimExodus build failed ($LASTEXITCODE)." }
} finally { Pop-Location }
