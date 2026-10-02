param(
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'Source'
[xml]$project = Get-Content -LiteralPath (Join-Path $source 'MorphocyteRouter/MorphocyteRouter.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts/$version" }
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or
        @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
        throw 'Choose an empty release directory. Existing files will not be removed.'
    }
}

$appName = "MorphocyteOS-$version-win-x64"
$package = Join-Path $output $appName
& (Join-Path $source 'build.ps1') -OutputPath $package -SkipTests:$SkipTests -NoRestore:$NoRestore

# Reject unexpected files rather than silently packaging a user's working directory.
$rootFiles = @('MorphocyteOS.exe', 'mihomo.exe', 'release-source.json', 'release-manifest.json', 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md')
$packageFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File -Force)
foreach ($file in $packageFiles) {
    $relative = $file.FullName.Substring($package.Length + 1).Replace('\', '/')
    if ($relative -notin $rootFiles -and $relative -notmatch '^(Licenses|ThirdPartySource)/') {
        throw "Unexpected file in release package: $relative"
    }
    if ($file.Name -match '^(settings\.json|portable\.flag|.*\.log|.*\.ya?ml|.*\.bak.*)$') {
        throw "Local state is forbidden in release package: $relative"
    }
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked file: $relative" }
}
foreach ($name in $rootFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) { throw "Missing release file: $name" }
}
$binaryVersion = (Get-Item -LiteralPath (Join-Path $package 'MorphocyteOS.exe')).VersionInfo.ProductVersion
if ($binaryVersion -ne $version) { throw "Binary version mismatch: $binaryVersion / $version" }
$sourceArchive = Join-Path $package 'ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz'
if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash -ne 'F52777EFC150719141ECC8F30071CD5DFDAAA770E9E4AD19A6A4EC3CBD583D2B') {
    throw 'Mihomo corresponding-source archive hash mismatch.'
}

# Build source assets from a fixed list, never from all files in the repository.
$sourceStage = Join-Path $output "MorphocyteOS-$version-source"
New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
$rootSourceFiles = @('.gitignore', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'BUILDING.md', 'RELEASE-NOTES.md')
foreach ($name in $rootSourceFiles) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $sourceStage $name) }
$sourceDirectories = @('Source/MorphocyteRouter', 'Source/RouterRegressionTests', 'Source/RouterIntegrationTests',
    'Source/UpdateServiceTests', 'Source/UpdaterTests', 'Source/FakeMihomo', 'Source/Packaging')
foreach ($directory in $sourceDirectories) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo $directory) -Recurse -File -Force) {
        $relative = $file.FullName.Substring($repo.Length + 1).Replace('\', '/')
        if ($relative -match '/(bin|obj|artifacts|\.preview)/') { continue }
        $allowedSource = $relative -match '^Source/MorphocyteRouter/[^/]+\.(cs|xaml|csproj)$' -or
            $relative -in @('Source/MorphocyteRouter/app.manifest', 'Source/MorphocyteRouter/Assets/profile-template.yaml',
                'Source/MorphocyteRouter/Assets/morphocyte.ico', 'Source/MorphocyteRouter/Assets/morphocyte-icon.png') -or
            $relative -match '^Source/(RouterRegressionTests|RouterIntegrationTests|UpdateServiceTests|UpdaterTests|FakeMihomo)/[^/]+\.(cs|csproj)$' -or
            $relative -match '^Source/Packaging/Licenses/' -or
            $relative -in @('Source/Packaging/Engine/mihomo.exe', 'Source/Packaging/LICENSE.txt',
                'Source/Packaging/THIRD-PARTY-NOTICES.md', 'Source/Packaging/ThirdPartySource/Build-Mihomo.ps1',
                'Source/Packaging/ThirdPartySource/README.md', 'Source/Packaging/ThirdPartySource/mihomo-v1.19.31.tar.gz',
                'Source/Packaging/ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz')
        if (-not $allowedSource) { throw "Unexpected source file: $relative" }
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked source file: $relative" }
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
foreach ($name in @('Source/build.ps1', 'Source/global.json', 'Source/release-source.json', 'Source/README.md', 'Scripts/Release.ps1')) {
    $destination = Join-Path $sourceStage $name
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $destination
}

$appZip = Join-Path $output "$appName.zip"
$sourceZip = Join-Path $output "MorphocyteOS-$version-source.zip"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-ReleaseArchive {
    param([string]$Directory, [string]$Destination)
    $archiveRoot = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $prefix = [System.IO.Path]::GetFileName($archiveRoot) + '/'
    $zip = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $archiveRoot -Recurse -File -Force | Sort-Object FullName) {
            $relative = $file.FullName.Substring($archiveRoot.Length + 1).Replace('\', '/')
            # Explicit slash paths work in both Windows PowerShell 5 and PowerShell 7.
            # Compress-Archive in older Windows versions writes backslash entries.
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ($prefix + $relative), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
New-ReleaseArchive -Directory $package -Destination $appZip
New-ReleaseArchive -Directory $sourceStage -Destination $sourceZip
$archive = [System.IO.Compression.ZipFile]::OpenRead($sourceZip)
try {
    foreach ($name in @('LICENSE', 'BUILDING.md', 'Source/MorphocyteRouter/MorphocyteRouter.csproj',
        'Source/MorphocyteRouter/ProcessLifetimeJob.cs', 'Source/MorphocyteRouter/Assets/profile-template.yaml',
        'Source/Packaging/ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz')) {
        if (-not $archive.GetEntry("MorphocyteOS-$version-source/$name")) { throw "Missing source archive entry: $name" }
    }
}
finally { $archive.Dispose() }
# Only remove the staging directory created by this run, after verifying its archive.
$stagePath = [System.IO.Path]::GetFullPath($sourceStage)
if ([System.IO.Path]::GetDirectoryName($stagePath) -ne $output -or
    [System.IO.Path]::GetFileName($stagePath) -ne "MorphocyteOS-$version-source") { throw 'Unsafe staging path.' }
Remove-Item -LiteralPath $stagePath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'RELEASE-NOTES.md') -Destination (Join-Path $output 'RELEASE-NOTES.md')
$hashLines = foreach ($file in @($appZip, $sourceZip)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($file))"
}
[System.IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Release prepared: $output"
Write-Host 'No files were uploaded. Test the app from a separate copy before publishing.'
