#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PackageRoot = "$PSScriptRoot/../Screenbox/AppPackages",
    [string]$OutputRoot = "$PSScriptRoot/../artifacts"
)
$ErrorActionPreference = "Stop"
$package = Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
    Where-Object { $_.Extension -in ".msix", ".appx" -and $_.Name -match "_x64" -and $_.Directory.Name -ne "x64" } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $package) { throw "No x64 application package was produced." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archiveCheck = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $worker = $archiveCheck.Entries | Where-Object { $_.FullName.Replace("\\", "/") -eq "MpvBridge/Screenbox.MpvBridge.exe" } | Select-Object -First 1
    if (-not $worker -or $worker.Length -eq 0) { throw "The packaged renderer executable is missing." }
    $manifestEntry = $archiveCheck.GetEntry("AppxManifest.xml")
    if (-not $manifestEntry) { throw "Package manifest is missing." }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.Package.Identity.Name -ne "Screenbox.MpvPreview") { throw "Unexpected package identity." }
    $service = $manifest.SelectSingleNode("//*[local-name()='AppService' and @Name='Screenbox.Mpv']")
    $launcher = $manifest.SelectSingleNode("//*[local-name()='Extension' and @Category='windows.fullTrustProcess']")
    if (-not $service -or -not $launcher -or $launcher.Executable -ne "MpvBridge\Screenbox.MpvBridge.exe") {
        throw "The app service and full trust renderer must be present in the packaged manifest."
    }
    Write-Host "Verified packaged renderer, identity and app service."
}
finally { $archiveCheck.Dispose() }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("ScreenboxMpvKit-" + [Guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $stage
try {
    Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $stage ("ScreenboxMpvPreview" + $package.Extension))
    $dependencyRoot = Join-Path $package.Directory.FullName "Dependencies"
    if (Test-Path -LiteralPath $dependencyRoot) {
        $destination = Join-Path $stage "Dependencies"
        $null = New-Item -ItemType Directory -Path $destination
        foreach ($architecture in "x64", "neutral") {
            $source = Join-Path $dependencyRoot $architecture
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $destination -Recurse }
        }
        Get-ChildItem -LiteralPath $dependencyRoot -File |
            Where-Object { $_.Extension -in ".appx", ".msix" } |
            Copy-Item -Destination $destination
    }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Copy-Item -LiteralPath (Join-Path $repository "scripts/mpv-preview/Install.cmd") -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repository "docs/MPV-PREVIEW.zh-CN.md") -Destination (Join-Path $stage "README.zh-CN.md")
    Copy-Item -LiteralPath (Join-Path $repository "LICENSE") -Destination $stage
    $commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (& git rev-parse HEAD).Trim() }
    @(
        "Screenbox mpv Preview - modified build of Screenbox by Tung Huynh",
        "License: GNU GPL version 3 or later. See LICENSE.",
        "Source commit: https://github.com/ywz3135340080-glitch/Screenbox/tree/$commit",
        "Source archive: https://github.com/ywz3135340080-glitch/Screenbox/archive/$commit.zip",
        "Build workflow: .github/workflows/mpv-development.yml",
        "mpv is selected from an existing local installation and is not bundled."
    ) | Set-Content -LiteralPath (Join-Path $stage "SOURCE.txt") -Encoding utf8
    $null = New-Item -ItemType Directory -Path $OutputRoot -Force
    $archive = Join-Path $OutputRoot "ScreenboxMpvPreview-x64.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
    Write-Host "Install kit: $archive"
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedStage).StartsWith("ScreenboxMpvKit-")) {
        throw "Refusing to remove an unexpected staging directory."
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
