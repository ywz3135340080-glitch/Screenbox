param([string]$PackageName = 'Screenbox.ColorPreview', [switch]$RequireReadyLog)
$ErrorActionPreference = 'Stop'
$started = Get-Date
$package = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if (-not $package) { throw 'The preview app was not installed.' }
$manifest = Get-AppxPackageManifest -Package $package
$appId = $package.PackageFamilyName + '!' + $manifest.Package.Applications.Application.Id
$state = Join-Path $env:LOCALAPPDATA ("Packages/" + $package.PackageFamilyName + "/LocalState")
New-Item -ItemType Directory -Path $state -Force | Out-Null
Set-Content -LiteralPath (Join-Path $state 'color-self-test.request') -Value '1'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPreviewActivationManager
{
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    [PreserveSig] int ActivateForFile(IntPtr itemArray, string verb, out uint processId);
    [PreserveSig] int ActivateForProtocol(IntPtr itemArray, out uint processId);
}
public static class PreviewActivation
{
    public static uint Launch(string appId)
    {
        var type = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"));
        var manager = (IPreviewActivationManager)Activator.CreateInstance(type);
        uint processId;
        int result = manager.ActivateApplication(appId, "", 0, out processId);
        Marshal.ReleaseComObject(manager);
        Marshal.ThrowExceptionForHR(result);
        return processId;
    }
}
'@
try {
    $playerProcessId = [PreviewActivation]::Launch($appId)
    "Activated $appId as PID $playerProcessId"
    $process = Get-Process -Id $playerProcessId
    if ($process.WaitForExit(15000)) { throw "The app exited during startup: $($process.ExitCode)." }
    if ($RequireReadyLog) {
        $readyPath = Join-Path $env:LOCALAPPDATA ("Packages/" + $package.PackageFamilyName + "/LocalState/startup.log")
        if (-not (Test-Path -LiteralPath $readyPath) -or
            (Get-Item -LiteralPath $readyPath).LastWriteTime -lt $started -or
            -not ((Get-Content -LiteralPath $readyPath -Raw) -match 'Main window ready')) {
            throw 'The UI did not confirm that the main window was activated.'
        }
    }
    $nativeLog = Join-Path $state 'color-self-test.log'
    if (-not (Test-Path -LiteralPath $nativeLog) -or
        (Get-Item -LiteralPath $nativeLog).LastWriteTime -lt $started -or
        (Get-Content -LiteralPath $nativeLog -Raw).Trim() -ne 'Native color self-test: 0') {
        if (Test-Path -LiteralPath $nativeLog) { Get-Content -LiteralPath $nativeLog }
        throw 'The native color component did not pass its check inside the installed UWP app.'
    }
    'PASS: the installed player stayed running for 15 seconds and its native color component rendered P010 successfully.'
} finally {
    $diagnostics = Join-Path $PWD 'artifacts/startup-check'
    $null = New-Item -ItemType Directory -Path $diagnostics -Force
    $state = Join-Path $env:LOCALAPPDATA ("Packages/" + $package.PackageFamilyName + "/LocalState")
    if (Test-Path -LiteralPath $state) {
        Get-ChildItem -LiteralPath $state -File | Where-Object { $_.Name -match 'startup|color-self-test' } | Copy-Item -Destination $diagnostics
    }
    foreach ($logName in @('Application','Microsoft-Windows-TWinUI/Operational')) {
        try {
            Get-WinEvent -FilterHashtable @{LogName=$logName;StartTime=$started} -ErrorAction Stop |
                Where-Object { $_.Message -match 'Screenbox|ColorPreview' } |
                Format-List TimeCreated,Id,Message | Out-String -Width 240 |
                Add-Content -LiteralPath (Join-Path $diagnostics 'events.txt')
        } catch { "Event log $logName : $_" | Add-Content -LiteralPath (Join-Path $diagnostics 'events.txt') }
    }
}
