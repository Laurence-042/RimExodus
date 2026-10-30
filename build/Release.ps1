param([ValidateSet('1.6','1.7')][string]$Version='1.7')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
& "$PSScriptRoot/Build.ps1" -Version $Version -Configuration Release
$parent = Split-Path $root -Parent
$target = [IO.Path]::GetFullPath((Join-Path $parent 'RimExodus.release'))
if ((Split-Path $target -Parent) -ne $parent -or (Split-Path $target -Leaf) -ne 'RimExodus.release') {
    throw "Unexpected release destination: $target"
}
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to replace a linked release directory.'
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path "$target/About" -Force | Out-Null
$about = [IO.File]::ReadAllText("$root/About/About.xml")
$about = $about.Replace('Seamless World Dev<', 'Seamless World<').Replace('.SeamlessWorld.Dev<', '.SeamlessWorld<')
$about = [regex]::Replace($about, '(?s)<supportedVersions>.*?</supportedVersions>', "<supportedVersions><li>$Version</li></supportedVersions>")
[IO.File]::WriteAllText("$target/About/About.xml", $about)
foreach ($file in @('Preview.png', 'PublishedFileId.txt')) {
    if (Test-Path -LiteralPath "$root/About/$file") { Copy-Item -LiteralPath "$root/About/$file" -Destination "$target/About" }
}
Copy-Item -LiteralPath "$root/$Version" -Destination "$target/$Version" -Recurse
Get-ChildItem -LiteralPath "$target/$Version/Assemblies" -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath "$root/Textures" -Destination "$target/Textures" -Recurse
Copy-Item -LiteralPath "$root/LICENSE" -Destination $target
Copy-Item -LiteralPath "$root/LoadFolders.xml" -Destination $target
Write-Output "Packaged RimExodus $Version at $target. Install RimExodus Framework separately."
