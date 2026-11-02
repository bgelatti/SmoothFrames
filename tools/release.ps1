# Builds Smooth Frames, packs the release zip and publishes it as a GitHub release.
# Usage: pwsh tools/release.ps1 [-GamePath "C:\path\to\Apocalypter"] [-DryRun]
param(
    [string]$GamePath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Fail($msg) { Write-Error $msg; exit 1 }

[xml]$proj = Get-Content 'SmoothFrames.csproj'
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { Fail 'No <Version> in SmoothFrames.csproj.' }
$tag = "v$version"

# Changelog section "## <version>" up to the next "## "
$notes = @()
$inSection = $false
foreach ($line in Get-Content 'CHANGELOG.md') {
    if ($line -match '^## ') {
        if ($inSection) { break }
        $inSection = $line.Trim() -eq "## $version"
        continue
    }
    if ($inSection) { $notes += $line }
}
$notes = ($notes -join "`n").Trim()
if (-not $notes) { Fail "CHANGELOG.md has no '## $version' section." }

if (-not $DryRun) {
    if (git status --porcelain) { Fail 'Working tree is not clean. Commit first.' }
    git fetch --tags --quiet
    if (git tag --list $tag) { Fail "Tag $tag already exists. Bump <Version> first." }
}

# Build
$buildArgs = @('build', '-c', 'Release', '--nologo')
if ($GamePath) { $buildArgs += "-p:GamePath=$GamePath" }
dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { Fail 'Build failed.' }

# Pack: zip contains a SmoothFrames folder, ready for BepInEx\plugins
$staging = Join-Path $root 'release\staging'
$pluginDir = Join-Path $staging 'SmoothFrames'
$zip = Join-Path $root "release\SmoothFrames-$version.zip"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item 'bin\Release\SmoothFrames.dll', 'assets\icon.png', 'README.md', 'LICENSE', 'CHANGELOG.md' $pluginDir
Compress-Archive -Path $pluginDir -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Write-Host "Packed $zip"

if ($DryRun) { Write-Host 'Dry run: not tagging or publishing.'; exit 0 }

$notesFile = Join-Path $root 'release\notes.md'
Set-Content $notesFile $notes -Encoding utf8

git tag -a $tag -m "Smooth Frames $version"
git push origin $tag
if ($LASTEXITCODE -ne 0) { Fail "Could not push tag $tag." }

gh release create $tag $zip --title "Smooth Frames $version" --notes-file $notesFile --verify-tag
if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed.' }
Write-Host "Released $tag"
