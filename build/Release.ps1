$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$parent = Split-Path $root -Parent
$frameworkRoot = [IO.Path]::GetFullPath((Join-Path $parent 'RimExodusFramework'))
. (Join-Path $frameworkRoot 'build/ReleaseBuild.ps1')
$results = @()
foreach ($version in @('1.6', '1.7')) {
    $results += Invoke-ReleaseVersionBuild -ProjectRoot $root -Version $version `
        -AssemblyName 'RimExodus.dll' -BuildScript "$PSScriptRoot/Build.ps1" -Configuration 'Release' `
        -AdditionalInputDirs @((Join-Path $frameworkRoot 'Source')) `
        -AdditionalInputFiles @((Join-Path $frameworkRoot "$version/Assemblies/RimExodus.Maps.dll"), `
                                (Join-Path $frameworkRoot 'build/Build.ps1'))
}
if (@($results | Where-Object { !$_.Usable }).Count -gt 0) {
    throw 'Release aborted: both current game-version assemblies are required.'
}
$target = [IO.Path]::GetFullPath((Join-Path $parent 'RimExodus.release'))
if ((Split-Path $target -Parent) -ne $parent -or (Split-Path $target -Leaf) -ne 'RimExodus.release') {
    throw "Unexpected release destination: $target"
}
if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to replace a linked release directory.'
}
$about = New-Object System.Xml.XmlDocument
$about.PreserveWhitespace = $true
$about.Load((Join-Path $root 'About/About.xml'))
if ($about.ModMetaData.packageId -ne 'RimExodus.SeamlessWorld.Dev') {
    throw "Unexpected development packageId: $($about.ModMetaData.packageId)"
}
if (!$about.ModMetaData.name.EndsWith(' (Dev)')) { throw 'Development name marker missing.' }
$about.ModMetaData.name = $about.ModMetaData.name.Substring(0, $about.ModMetaData.name.Length - ' (Dev)'.Length)
$about.ModMetaData.packageId = 'RimExodus.SeamlessWorld'
$framework = $about.ModMetaData.modDependencies.SelectSingleNode("li[packageId='RimExodus.Framework.dev']")
if ($null -eq $framework) { throw 'Development REF dependency missing.' }
$framework.packageId = 'RimExodus.Framework'
$framework.displayName = 'RimExodus Framework'
$after = $about.ModMetaData.loadAfter.SelectSingleNode("li[.='RimExodus.Framework.dev']")
if ($null -eq $after) { throw 'Development REF load order entry missing.' }
$after.InnerText = 'RimExodus.Framework'
if (($about.ModMetaData.supportedVersions.li -join ',') -ne '1.6,1.7') {
    throw 'Release metadata must support both 1.6 and 1.7.'
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path "$target/About" -Force | Out-Null
$about.Save((Join-Path $target 'About/About.xml'))
foreach ($file in @('Preview.png', 'PublishedFileId.txt')) {
    if (Test-Path -LiteralPath "$root/About/$file") { Copy-Item -LiteralPath "$root/About/$file" -Destination "$target/About" }
}
foreach ($version in @('1.6', '1.7')) {
    Copy-Item -LiteralPath "$root/$version" -Destination "$target/$version" -Recurse
    Get-ChildItem -LiteralPath "$target/$version/Assemblies" -Filter '*.pdb' | Remove-Item -Force
}
Copy-Item -LiteralPath "$root/Textures" -Destination "$target/Textures" -Recurse
Copy-Item -LiteralPath "$root/LICENSE" -Destination $target
Copy-Item -LiteralPath "$root/LoadFolders.xml" -Destination $target
Write-Output "Packaged RimExodus 1.6 and 1.7 at $target. Install RimExodus Framework separately."
