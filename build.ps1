# Packages self-contained OpenForge builds into dist\
#
#   .\build.ps1                               all targets, version from src\OpenForge.csproj
#   .\build.ps1 -Version 1.2.0                override the version
#   .\build.ps1 -Targets linux-x64,osx-arm64  only the listed targets
param(
    [string]$Version = "",
    [string[]]$Targets = @()
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Project = Join-Path $Root "src/OpenForge.csproj"
$Dist = Join-Path $Root "dist"
$AllTargets = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")

if ($Targets.Count -eq 0) { $Targets = $AllTargets }
if (-not $Version) { $Version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

foreach ($target in $Targets) {
    if ($AllTargets -notcontains $target) { throw "Unknown target '$target'. Choose from: $($AllTargets -join ', ')" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET 10 SDK is required: https://dotnet.microsoft.com/download" }
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

foreach ($target in $Targets) {
    $name = "openforge-$Version-$target"
    $out = Join-Path $Dist $name
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "==> $name"

    dotnet publish $Project `
        --configuration Release `
        --runtime $target `
        --self-contained true `
        --output $out `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:GenerateDocumentationFile=false `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $target" }

    Copy-Item (Join-Path $Root "LICENSE"), (Join-Path $Root "README.md") $out
    Get-ChildItem $out -Recurse -Filter "*.pdb" | Remove-Item

    # Windows can't record the Unix executable bit, so Linux/macOS packages made here need `chmod +x OpenForge` after extracting.
    if ($target -like "win-*") {
        $zip = Join-Path $Dist "$name.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path $out -DestinationPath $zip
    }
    else {
        tar -czf (Join-Path $Dist "$name.tar.gz") -C $Dist $name
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $target" }
    }

    Remove-Item -Recurse -Force $out
}

$sums = Get-ChildItem $Dist -Filter "openforge-$Version-*" | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
$sums | Set-Content (Join-Path $Dist "SHA256SUMS-$Version.txt")
Write-Host ""
Write-Host "Packages in ${Dist}:"
Get-ChildItem $Dist -Filter "*$Version*" | ForEach-Object { $_.Name }
