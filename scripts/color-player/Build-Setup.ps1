[CmdletBinding()]
param(
 [string]$Payload="$PSScriptRoot/../../artifacts/ScreenboxHDR-x64.zip",
 [string]$Output="$PSScriptRoot/../../artifacts/ScreenboxHDR-Setup.exe",
 [Parameter(Mandatory=$true)][string]$SourceCommit
)
$ErrorActionPreference="Stop"
$compiler=Join-Path $env:WINDIR "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
if(-not(Test-Path -LiteralPath $compiler)){throw "Windows compiler was not found."}
$null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Output))) -Force
$source=Join-Path $env:TEMP ("ScreenboxSetupSource-"+[Guid]::NewGuid().ToString("N")+".txt")
try {
 @(
  "Screenbox HDR, based on Screenbox by Tung Huynh",
  "GNU GPL version 3 or later",
  "Source: https://github.com/ywz3135340080-glitch/Screenbox/tree/$SourceCommit",
  "Source archive: https://github.com/ywz3135340080-glitch/Screenbox/archive/$SourceCommit.zip"
 ) | Set-Content -LiteralPath $source -Encoding utf8
 $Payload=[IO.Path]::GetFullPath($Payload)
 $Output=[IO.Path]::GetFullPath($Output)
 $code=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "Setup.cs")).Path
 $manifest=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "Setup.manifest")).Path
 $engine=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "InstallEngine.ps1")).Path
 $arguments=@(
  "/nologo","/target:winexe","/platform:x64","/optimize+","/codepage:65001",
  "/out:$Output","/win32manifest:$manifest",
  "/reference:System.Windows.Forms.dll","/reference:System.Drawing.dll",
  "/reference:System.IO.Compression.dll","/reference:System.IO.Compression.FileSystem.dll",
  "/resource:$Payload,ScreenboxPayload.zip","/resource:$engine,InstallEngine.ps1",
  "/resource:$source,SetupSource.txt",$code
 )
 & $compiler @arguments
 if($LASTEXITCODE -ne 0){throw "Installer compilation failed."}
}
finally {if(Test-Path -LiteralPath $source){Remove-Item -LiteralPath $source}}
