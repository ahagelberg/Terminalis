# PowerShell script to build and package Terminalis for release
# Usage: .\publish-release.ps1 [version]
# Version can be with or without 'v' prefix: v1.1.0 or 1.1.0

param(
    [string]$Version = "1.1.0"
)

$ErrorActionPreference = "Stop"

# Normalize version: remove 'v' prefix if present, then add it back
if ($Version.StartsWith("v")) {
    $Version = $Version.Substring(1)
}
$VersionTag = "v$Version"

Write-Host "Building Terminalis $VersionTag for release..." -ForegroundColor Green

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean -c Release

# Publish as self-contained Windows executable
Write-Host "Publishing Windows x64 release..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o ".\publish\Terminalis-$VersionTag-win-x64"

# Create zip archive
Write-Host "Creating zip archive..." -ForegroundColor Yellow
$zipPath = ".\publish\Terminalis-$VersionTag-win-x64.zip"
Compress-Archive -Path ".\publish\Terminalis-$VersionTag-win-x64\*" -DestinationPath $zipPath -Force

Write-Host "`nRelease build complete!" -ForegroundColor Green
Write-Host "Output: $zipPath" -ForegroundColor Cyan
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "1. Test the executable in .\publish\Terminalis-$VersionTag-win-x64\" -ForegroundColor White
Write-Host "2. Create a Git tag: git tag $VersionTag" -ForegroundColor White
Write-Host "3. Push the tag: git push origin $VersionTag" -ForegroundColor White
Write-Host "4. Or manually create a GitHub Release and upload: $zipPath" -ForegroundColor White


