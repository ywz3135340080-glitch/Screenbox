$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Write-Output '[PROGRESS]35'
try {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ throw 'Installer elevation is required.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function Read-PackageManifest([string]$path) {
        $zip=[IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entry=$zip.GetEntry('AppxManifest.xml')
            if(-not $entry){ throw 'Package manifest is missing.' }
            $reader=[IO.StreamReader]::new($entry.Open())
            try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
    }
    $root=$env:SCREENBOX_KIT_DIR
    $package=@(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.BaseName -eq 'ScreenboxMpvPreview' -and $_.Extension -in '.msix','.appx' })
    if($package.Count -ne 1){ throw 'Extract the complete ZIP into a folder before installing.' }
    $manifest=Read-PackageManifest $package[0].FullName
    $already=@(Get-AppxPackage -Name $manifest.Package.Identity.Name | Where-Object { $_.Publisher -eq $manifest.Package.Identity.Publisher -and [version]$_.Version -ge [version]$manifest.Package.Identity.Version -and [string]$_.Status -eq 'Ok' })
    if($already.Count -eq 0) {
    $required=@($manifest.Package.Dependencies.PackageDependency | Where-Object { $_ })
    $missing=@()
    foreach($dependency in $required) {
        $installed=@(Get-AppxPackage -Name $dependency.Name | Where-Object {
            $_.Publisher -eq $dependency.Publisher -and
            [version]$_.Version -ge [version]$dependency.MinVersion -and
            [string]$_.Architecture -in 'X64','Neutral' -and [string]$_.Status -eq 'Ok'
        })
        if($installed.Count -eq 0){ $missing+= $dependency }
        else { Write-Host ('Reusing installed runtime: '+$dependency.Name) }
    }
    $dependencies=@()
    foreach($dependency in $missing) {
        $dependencyRoot=Join-Path $root 'Dependencies'
        $candidate=$null
        if(Test-Path -LiteralPath $dependencyRoot) {
            foreach($file in Get-ChildItem -LiteralPath $dependencyRoot -Recurse -File | Where-Object { $_.Extension -in '.appx','.msix' }) {
                $identity=(Read-PackageManifest $file.FullName).Package.Identity
                if($identity.Name -eq $dependency.Name -and $identity.Publisher -eq $dependency.Publisher -and [version]$identity.Version -ge [version]$dependency.MinVersion -and $identity.ProcessorArchitecture -in 'x64','neutral') {
                    $candidate=$file.FullName
                    break
                }
            }
        }
        if(-not $candidate){ throw ('A required runtime is missing from this folder: '+$dependency.Name+'. Extract the complete installation ZIP again.') }
        $dependencies+= $candidate
    }
    Write-Output '[PROGRESS]60'
    if($dependencies.Count -gt 0) {
        Add-AppxPackage -Path $package[0].FullName -DependencyPath $dependencies -AllowUnsigned
    } else {
        Add-AppxPackage -Path $package[0].FullName -AllowUnsigned
    }
    }
    $registered=@(Get-AppxPackage -Name $manifest.Package.Identity.Name | Where-Object { $_.Publisher -eq $manifest.Package.Identity.Publisher -and [string]$_.Status -eq 'Ok' })
    if($registered.Count -eq 0){ throw 'The application was not registered after deployment.' }
    $application=@($manifest.Package.Applications.Application)[0]
    Write-Output ('[AUMID]'+$registered[0].PackageFamilyName+'!'+$application.Id)
    Write-Output '[PROGRESS]100'
    exit 0
} catch {
    Write-Output ('[ERROR]'+$_.Exception.Message)
    Write-Output ('[DETAIL]'+$_.ToString())
    exit 1
}
