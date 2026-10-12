param(
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$NoRestore,
    [switch]$WithInstaller,
    [switch]$TestInstaller,
    [string]$InstallerCompilerPath
)

$ErrorActionPreference = 'Stop'
if ($TestInstaller -and -not $WithInstaller) { throw 'TestInstaller requires WithInstaller.' }
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
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'Test-UpdateAssets.ps1') }
& (Join-Path $source 'build.ps1') -OutputPath $package -SkipTests:$SkipTests -NoRestore:$NoRestore

# Reject unexpected files rather than silently packaging a user's working directory.
$rootFiles = @('MorphocyteOS.exe', 'mihomo.exe', 'release-source.json', 'release-manifest.json', 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md')
$packageFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File -Force)
foreach ($file in $packageFiles) {
    $relative = $file.FullName.Substring($package.Length + 1).Replace('\', '/')
    if ($relative -notin $rootFiles -and $relative -notmatch '^(Licenses|ThirdPartySource)/') {
        throw "Unexpected file in release package: $relative"
    }
    if ($file.Name -match '^(settings\.json|portable\.flag|.*\.log|.*\.ya?ml|.*\.bak.*|.*\.db)$') {
        throw "Local state is forbidden in release package: $relative"
    }
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked file: $relative" }
}
foreach ($name in $rootFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) { throw "Missing release file: $name" }
}
$binaryVersion = (Get-Item -LiteralPath (Join-Path $package 'MorphocyteOS.exe')).VersionInfo.ProductVersion
if ($binaryVersion -ne $version) { throw "Binary version mismatch: $binaryVersion / $version" }
if ((Get-FileHash -LiteralPath (Join-Path $package 'ThirdPartySource/Engine/sing-box.exe') -Algorithm SHA256).Hash -ne '7BBEF1DEA9189EE12799AE834EA4B4658355DA25C47A21AD8804904C0CCD9410') { throw 'sing-box engine hash mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $package 'ThirdPartySource/sing-box-v1.14.2-corresponding-source.tar.gz') -Algorithm SHA256).Hash -ne 'C2B3D9AE082A364AA082E120931B1DF44E68E63AA27C7CA51201B4AE19660D75') { throw 'sing-box corresponding-source hash mismatch.' }
$sourceArchive = Join-Path $package 'ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz'
if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash -ne 'F52777EFC150719141ECC8F30071CD5DFDAAA770E9E4AD19A6A4EC3CBD583D2B') {
    throw 'Mihomo corresponding-source archive hash mismatch.'
}

# Build source assets from a fixed list, never from all files in the repository.
if ($WithInstaller) {
    $artworkArguments = @('run', '--project', (Join-Path $source 'InstallerArtworkBuilder/InstallerArtworkBuilder.csproj'), '-c', 'Release')
    if ($NoRestore) { $artworkArguments += '--no-restore' }
    $artworkArguments += @('--', '--input', (Join-Path $source 'MorphocyteRouter/Assets/morphocyte-icon.png'),
        '--outputdir', (Join-Path $repo 'Installer/Assets'))
    & dotnet @artworkArguments
    if ($LASTEXITCODE -ne 0) { throw 'Installer artwork generation failed.' }
}
$sourceStage = Join-Path $output "MorphocyteOS-$version-source"
New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
$rootSourceFiles = @('.gitignore', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'BUILDING.md', 'RELEASE-NOTES.md')
foreach ($name in $rootSourceFiles) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $sourceStage $name) }
$sourceDirectories = @('Source/MorphocyteRouter', 'Source/IconAssetBuilder', 'Source/InstallerArtworkBuilder', 'Source/SubscriptionImportTests', 'Source/RouterRegressionTests', 'Source/RouterIntegrationTests',
    'Source/UpdateServiceTests', 'Source/UpdaterTests', 'Source/InstallerTests', 'Source/InstallerHelper', 'Source/FakeMihomo', 'Source/Packaging')
foreach ($directory in $sourceDirectories) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo $directory) -Recurse -File -Force) {
        $relative = $file.FullName.Substring($repo.Length + 1).Replace('\', '/')
        if ($relative -match '/(bin|obj|artifacts|\.preview)/') { continue }
        $allowedSource = $relative -match '^Source/MorphocyteRouter/[^/]+\.(cs|xaml|csproj)$' -or
            $relative -in @('Source/MorphocyteRouter/app.manifest', 'Source/MorphocyteRouter/Assets/profile-template.yaml',
                'Source/MorphocyteRouter/Assets/morphocyte.ico', 'Source/MorphocyteRouter/Assets/morphocyte-icon.png') -or
            $relative -match '^Source/(IconAssetBuilder|InstallerArtworkBuilder|SubscriptionImportTests|RouterRegressionTests|RouterIntegrationTests|UpdateServiceTests|UpdaterTests|InstallerTests|InstallerHelper|FakeMihomo)/[^/]+\.(cs|csproj)$' -or
            $relative -match '^Source/Packaging/Licenses/' -or
            $relative -in @('Source/Packaging/Engine/mihomo.exe', 'Source/Packaging/Engine/sing-box.exe', 'Source/Packaging/LICENSE.txt',
                'Source/Packaging/THIRD-PARTY-NOTICES.md', 'Source/Packaging/ThirdPartySource/Build-Mihomo.ps1',
                'Source/Packaging/ThirdPartySource/README.md', 'Source/Packaging/ThirdPartySource/mihomo-v1.19.31.tar.gz',
                'Source/Packaging/ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz',
                'Source/Packaging/ThirdPartySource/sing-box-v1.14.2.tar.gz',
                'Source/Packaging/ThirdPartySource/sing-box-v1.14.2-corresponding-source.tar.gz',
                'Source/Packaging/ThirdPartySource/Build-SingBox.ps1')
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
foreach ($name in @('Scripts/UpdateAssets.ps1', 'Scripts/Test-UpdateAssets.ps1', 'Scripts/Build-Installer.ps1',
    'Scripts/Test-Installer.ps1', 'Installer/MorphocyteOS.iss', 'Installer/UninstallBootstrap.iss', 'Installer/Branding.iss',
    'Installer/WindowChrome.iss', 'Installer/RichTextTheme.iss', 'Installer/InstallDirectory.iss', 'Installer/Assets/wizard-banner.png', 'Installer/Assets/wizard-banner-light.png',
    'Installer/Assets/wizard-mark.png', 'Installer/Assets/wizard-background.png', 'Installer/Assets/wizard-background-light.png')) {
    New-Item -ItemType Directory -Path (Split-Path -Parent (Join-Path $sourceStage $name)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $sourceStage $name)
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
    foreach ($name in @('LICENSE', 'BUILDING.md', 'Scripts/UpdateAssets.ps1', 'Scripts/Test-UpdateAssets.ps1',
        'Scripts/Build-Installer.ps1', 'Scripts/Test-Installer.ps1', 'Installer/MorphocyteOS.iss', 'Installer/UninstallBootstrap.iss',
        'Installer/Branding.iss', 'Installer/WindowChrome.iss', 'Installer/RichTextTheme.iss', 'Installer/InstallDirectory.iss', 'Installer/Assets/wizard-banner.png', 'Installer/Assets/wizard-banner-light.png',
        'Installer/Assets/wizard-mark.png', 'Installer/Assets/wizard-background.png', 'Installer/Assets/wizard-background-light.png',
        'Source/InstallerArtworkBuilder/InstallerArtworkBuilder.csproj', 'Source/InstallerArtworkBuilder/Program.cs',
        'Source/InstallerHelper/InstallerHelper.csproj', 'Source/InstallerTests/InstallerTests.csproj',
        'Source/MorphocyteRouter/InstallerMaintenance.cs', 'Source/MorphocyteRouter/MorphocyteRouter.csproj',
        'Source/MorphocyteRouter/ProcessLifetimeJob.cs', 'Source/MorphocyteRouter/Assets/profile-template.yaml',
        'Source/MorphocyteRouter/IconArtwork.cs', 'Source/IconAssetBuilder/IconAssetBuilder.csproj', 'Source/IconAssetBuilder/Program.cs',
        'Source/Packaging/ThirdPartySource/mihomo-v1.19.31-corresponding-source.tar.gz',
        'Source/Packaging/Engine/sing-box.exe', 'Source/MorphocyteRouter/SingBoxDns.cs',
        'Source/Packaging/ThirdPartySource/sing-box-v1.14.2-corresponding-source.tar.gz')) {
        if (-not $archive.GetEntry("MorphocyteOS-$version-source/$name")) { throw "Missing source archive entry: $name" }
    }
}
finally { $archive.Dispose() }
. (Join-Path $PSScriptRoot 'UpdateAssets.ps1')
$updateAssets = New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory $output -ExpectedVersion $version
$installerAssets = @()
if ($WithInstaller) {
    $helperStage = Join-Path $output ('.installer-helper-' + [Guid]::NewGuid().ToString('N'))
    $installerChecks = $null
    try {
        $helperArguments = @('publish', (Join-Path $source 'InstallerHelper/InstallerHelper.csproj'), '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', $helperStage)
        if ($NoRestore) { $helperArguments += '--no-restore' }
        & dotnet @helperArguments
        if ($LASTEXITCODE -ne 0) { throw 'Installer maintenance helper build failed.' }
        $helperPath = Join-Path $helperStage 'MorphocyteOS-maintenance.exe'
        if (-not $SkipTests) {
            $helperCheckArguments = @('run', '--project', (Join-Path $source 'InstallerTests/InstallerTests.csproj'), '-c', 'Release')
            if ($NoRestore) { $helperCheckArguments += '--no-restore' }
            $helperCheckArguments += @('--', '--verify-helper', $helperPath)
            & dotnet @helperCheckArguments
            if ($LASTEXITCODE -ne 0) { throw 'Published installer helper checks failed.' }
            $installerChecks = Join-Path $output ('.installer-checks-' + [Guid]::NewGuid().ToString('N'))
            & (Join-Path $PSScriptRoot 'Test-Installer.ps1') -PackageDirectory $package -MaintenanceHelperPath $helperPath -OutputDirectory $installerChecks
        }
        $installerArguments = @{ PackageDirectory = $package; OutputDirectory = $output; TestInstaller = [bool]$TestInstaller; MaintenanceHelperPath = $helperPath }
        if ($InstallerCompilerPath) { $installerArguments.IsccPath = $InstallerCompilerPath }
        $installer = & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @installerArguments
        if (-not $installer.OutputPath -or -not (Test-Path -LiteralPath $installer.OutputPath -PathType Leaf)) { throw 'Installer build did not return a setup executable.' }
        $installerAssets = @($installer.OutputPath)
    }
    finally {
        if ($installerChecks -and (Test-Path -LiteralPath $installerChecks)) {
            $resolvedChecks = [System.IO.Path]::GetFullPath($installerChecks)
            if ([System.IO.Path]::GetDirectoryName($resolvedChecks) -ne $output -or
                [System.IO.Path]::GetFileName($resolvedChecks) -notmatch '^\.installer-checks-[a-f0-9]{32}$') { throw 'Unsafe installer checks path.' }
            $checkEntries = @((Get-Item -LiteralPath $resolvedChecks -Force)) + @(Get-ChildItem -LiteralPath $resolvedChecks -Recurse -Force)
            if (@($checkEntries | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw 'Linked installer checks entry.' }
            Remove-Item -LiteralPath $resolvedChecks -Recurse -Force
        }
        $resolvedHelperStage = [System.IO.Path]::GetFullPath($helperStage)
        if ([System.IO.Path]::GetDirectoryName($resolvedHelperStage) -ne $output -or
            [System.IO.Path]::GetFileName($resolvedHelperStage) -notmatch '^\.installer-helper-[a-f0-9]{32}$') { throw 'Unsafe helper staging path.' }
        if (Test-Path -LiteralPath $resolvedHelperStage) {
            $helperEntries = @((Get-Item -LiteralPath $resolvedHelperStage -Force)) + @(Get-ChildItem -LiteralPath $resolvedHelperStage -Recurse -Force)
            if (@($helperEntries | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw 'Linked helper staging entry.' }
            Remove-Item -LiteralPath $resolvedHelperStage -Recurse -Force
        }
    }
}
# Only remove the staging directory created by this run, after verifying its archive.
$stagePath = [System.IO.Path]::GetFullPath($sourceStage)
if ([System.IO.Path]::GetDirectoryName($stagePath) -ne $output -or
    [System.IO.Path]::GetFileName($stagePath) -ne "MorphocyteOS-$version-source") { throw 'Unsafe staging path.' }
Remove-Item -LiteralPath $stagePath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'RELEASE-NOTES.md') -Destination (Join-Path $output 'RELEASE-NOTES.md')
$hashLines = foreach ($file in (@($appZip, $sourceZip, $updateAssets.IndexPath) + @($updateAssets.PackagePaths) + $installerAssets)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($file))"
}
[System.IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Release prepared: $output"
Write-Host "Incremental update: $($updateAssets.PackagePaths.Count) compressed components for $($updateAssets.FileCount) managed files."
Write-Host 'No files were uploaded. Test the app from a separate copy before publishing.'
