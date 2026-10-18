[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Payload)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
$fixture=Join-Path $tempRoot ('ScreenboxInstallerChecks-'+[Guid]::NewGuid().ToString('N'))
try {
 Expand-Archive -LiteralPath $Payload -DestinationPath $fixture
 $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $fixture 'ScreenboxMpvPreview.msix'))
 try {
  $reader=[IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
  try {[xml]$script:manifest=$reader.ReadToEnd()} finally {$reader.Dispose()}
 } finally {$archive.Dispose()}
 $script:required=@($script:manifest.Package.Dependencies.PackageDependency)
 $errors=$null
 $null=[Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/InstallEngine.ps1",[ref]$null,[ref]$errors)
 if($errors.Count -gt 0){throw ($errors | Out-String)}
 # The test invokes only mocks. Remove elevation and process-exit operations in memory.
 $engine=(Get-Content -LiteralPath "$PSScriptRoot/InstallEngine.ps1" | Where-Object {-not $_.Contains('$principal.IsInRole')}) -join [Environment]::NewLine
 $engine=$engine.Replace('exit 0','return').Replace('exit 1',"throw 'Installer engine failed'")
 function Get-AppxPackage {
  param([string]$Name)
  if($Name -eq 'Screenbox.MpvPreview'){
   if($script:deployed -or $script:mode -eq 'already-installed'){
    return [pscustomobject]@{Publisher=$script:manifest.Package.Identity.Publisher;Version=$script:manifest.Package.Identity.Version;Status='Ok';Architecture='X64';PackageFamilyName='Screenbox.MpvPreview_Test'}
   }
   return
  }
  foreach($dependency in $script:required){
   if($Name -eq $dependency.Name){
    if($script:mode -eq 'one-missing' -and $Name -eq 'Microsoft.UI.Xaml.2.8'){return}
    $version=[version]$dependency.MinVersion
    if($script:mode -eq 'one-outdated' -and $Name -eq 'Microsoft.UI.Xaml.2.8'){$version=[version]'1.0.0.0'}
    return [pscustomobject]@{Publisher=$dependency.Publisher;Version=$version;Status='Ok';Architecture='X64'}
   }
  }
 }
 function Add-AppxPackage {
  param([string]$Path,[string[]]$DependencyPath,[switch]$AllowUnsigned)
  if(-not $AllowUnsigned){throw 'The unsigned preview flag must be preserved.'}
  $script:deployments++
  $script:arguments=@($DependencyPath | Where-Object {$_})
  $script:deployed=$true
 }
 $env:SCREENBOX_KIT_DIR=$fixture
 foreach($case in @(
  @{Mode='all-satisfied';Dependencies=0;Deployments=1},
  @{Mode='one-missing';Dependencies=1;Deployments=1},
  @{Mode='one-outdated';Dependencies=1;Deployments=1},
  @{Mode='already-installed';Dependencies=0;Deployments=0}
 )){
  $script:mode=$case.Mode; $script:deployed=$false; $script:deployments=0; $script:arguments=@()
  & ([scriptblock]::Create($engine))
  if($script:deployments -ne $case.Deployments -or $script:arguments.Count -ne $case.Dependencies){throw ('Unexpected deployment selection: '+$case.Mode)}
  if($case.Dependencies -eq 1 -and [IO.Path]::GetFileName($script:arguments[0]) -ne 'Microsoft.UI.Xaml.2.8.appx'){throw 'An already satisfied unrelated framework was selected.'}
  Write-Host ('Installer scenario passed: '+$case.Mode)
 }
}
finally {
 $resolved=[IO.Path]::GetFullPath($fixture)
 if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolved).StartsWith('ScreenboxInstallerChecks-')){throw 'Unexpected fixture path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
