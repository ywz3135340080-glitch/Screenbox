[CmdletBinding()]
param([string]$PackageRoot="$PSScriptRoot/../../Screenbox/AppPackages", [string]$OutputRoot="$PSScriptRoot/../../artifacts")
$ErrorActionPreference="Stop"
$package=Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
 Where-Object { $_.Extension -in ".msix",".appx" -and $_.Name -match "_x64" -and $_.Directory.Name -ne "x64" } |
 Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(-not $package){throw "No application package was produced."}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
 if(-not $zip.GetEntry("Screenbox.NativeColor.dll")){throw "The packaged color component is missing."}
 $reader=[IO.StreamReader]::new($zip.GetEntry("AppxManifest.xml").Open())
 try{[xml]$manifest=$reader.ReadToEnd()}finally{$reader.Dispose()}
 if($manifest.Package.Identity.Name -ne "Screenbox.ColorPreview"){throw "Unexpected package identity."}
}finally{$zip.Dispose()}
$stage=Join-Path $OutputRoot "hdr-payload"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $stage "ScreenboxHDR.msix")
$dependencyRoot=Join-Path $package.Directory.FullName "Dependencies"
if(Test-Path -LiteralPath $dependencyRoot){
 $destination=Join-Path $stage "Dependencies"
 New-Item -ItemType Directory -Path $destination -Force | Out-Null
 foreach($architecture in "x64","neutral"){
  $source=Join-Path $dependencyRoot $architecture
  if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $destination -Recurse}
 }
 Get-ChildItem -LiteralPath $dependencyRoot -File | Where-Object {$_.Extension -in ".appx",".msix"} | Copy-Item -Destination $destination
}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
Copy-Item -LiteralPath (Join-Path $repo "LICENSE") -Destination $stage
@(
 "Screenbox HDR, modified from Screenbox by Tung Huynh",
 "License: GNU GPL version 3 or later. See LICENSE.",
 "Source commit: https://github.com/ywz3135340080-glitch/Screenbox/tree/$env:GITHUB_SHA",
 "Source archive: https://github.com/ywz3135340080-glitch/Screenbox/archive/$env:GITHUB_SHA.zip",
 "Native dependency notices are included inside the app package under NativeColorLicenses."
) | Set-Content -LiteralPath (Join-Path $stage "SOURCE.txt") -Encoding utf8
$archive=Join-Path $OutputRoot "ScreenboxHDR-x64.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
