[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Commit)
$ErrorActionPreference = "Stop"
$repo = "ywz3135340080-glitch/Screenbox"
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = "application/vnd.github+json" }
function Get-Github([string]$path) {
    Invoke-RestMethod -Uri ("https://api.github.com/repos/$repo/" + $path) -Headers $headers
}
$sourcePaths = @("native/color/screenbox-color.c", "native/color/screenbox-color.h", "native/color/build.sh")
$expected = @{}
foreach ($path in $sourcePaths) {
    $expected[$path] = (Get-Github "contents/$($path)?ref=$Commit").sha
}
$deadline = [DateTime]::UtcNow.AddMinutes(14)
$artifact = $null
while ([DateTime]::UtcNow -lt $deadline -and -not $artifact) {
    $runs = (Get-Github "actions/workflows/color-runtime.yml/runs?branch=codex%2Fdolby-vision&per_page=15").workflow_runs
    foreach ($run in $runs) {
        if ($run.conclusion -ne "success") { continue }
        $match = $true
        foreach ($path in $sourcePaths) {
            if ((Get-Github "contents/$($path)?ref=$($run.head_sha)").sha -ne $expected[$path]) { $match = $false; break }
        }
        if (-not $match) { continue }
        $artifact = (Get-Github "actions/runs/$($run.id)/artifacts").artifacts |
            Where-Object { $_.name -eq "screenbox-color-runtime-x64.7z" -and -not $_.expired } |
            Select-Object -First 1
        if ($artifact) { break }
    }
    if (-not $artifact) { Write-Host "Waiting for the matching, validated native component..."; Start-Sleep -Seconds 15 }
}
if (-not $artifact) { throw "No matching native component passed its tests." }
$archive = Join-Path $env:RUNNER_TEMP "screenbox-color-runtime.7z"
Invoke-WebRequest -Uri "https://api.github.com/repos/$repo/actions/artifacts/$($artifact.id)/zip" -Headers $headers -OutFile $archive
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ("sha256:$actual" -ne $artifact.digest) { throw "Native artifact checksum does not match." }
$destination = Join-Path $PWD "Screenbox/NativeColor"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& 7z x $archive "-o$destination" -y
if ($LASTEXITCODE -ne 0) { throw "Native archive extraction failed." }
Write-Host "Validated native artifact $($artifact.id): $actual"
