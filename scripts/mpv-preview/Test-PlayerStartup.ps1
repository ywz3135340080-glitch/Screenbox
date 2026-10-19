param([string]$PackageName = 'Screenbox.MpvPreview', [switch]$RequireReadyLog)
$ErrorActionPreference = 'Stop'
$started = Get-Date
$package = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if (-not $package) { throw 'The preview app was not installed.' }
$manifest = Get-AppxPackageManifest -Package $package
$appId = $package.PackageFamilyName + '!' + $manifest.Package.Applications.Application.Id
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
            -not ((Get-Content -LiteralPath $readyPath -Raw) -match 'Main window activated')) {
            throw 'The UI did not confirm that the main window was activated.'
        }
    }
    'PASS: the installed player stayed running for 15 seconds.'
} finally {
    $diagnostics = Join-Path $PWD 'artifacts/startup-check'
    $null = New-Item -ItemType Directory -Path $diagnostics -Force
    $state = Join-Path $env:LOCALAPPDATA ("Packages/" + $package.PackageFamilyName + "/LocalState")
    if (Test-Path -LiteralPath $state) {
        Get-ChildItem -LiteralPath $state -File -Filter '*startup*' | Copy-Item -Destination $diagnostics
    }
    foreach ($logName in @('Application','Microsoft-Windows-TWinUI/Operational')) {
        try {
            Get-WinEvent -FilterHashtable @{LogName=$logName;StartTime=$started} -ErrorAction Stop |
                Where-Object { $_.Message -match 'Screenbox|MpvPreview' } |
                Format-List TimeCreated,Id,Message | Out-String -Width 240 |
                Add-Content -LiteralPath (Join-Path $diagnostics 'events.txt')
        } catch { "Event log $logName : $_" | Add-Content -LiteralPath (Join-Path $diagnostics 'events.txt') }
    }
}
