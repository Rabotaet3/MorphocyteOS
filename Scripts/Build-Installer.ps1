param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$MaintenanceHelperPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$IsccPath,
    [switch]$TestInstaller,
    [switch]$PreviewOnly,
    [ValidateSet('system', 'light', 'dark')][string]$PreviewTheme = 'system',
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
if (-not $PreviewOnly -and $PreviewTheme -ne 'system') { throw 'A forced theme is only allowed for the non-installing preview.' }
$repo = Split-Path -Parent $PSScriptRoot
$package = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$output = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$manifestPath = Join-Path $package 'release-manifest.json'
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw 'Release package does not exist.' }

function Assert-NoLinkedPath {
    param([string]$Path)
    $current = [System.IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked paths are not allowed: $current" }
        }
        $parent = [System.IO.Path]::GetDirectoryName($current.TrimEnd('\', '/'))
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-RelativeReleasePath {
    param([string]$Path)
    $invalidSegments = @($Path.Split('/') | Where-Object {
        $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
        $_ -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'
    })
    if (-not $Path -or $Path.Length -gt 240 -or $Path -match '[\\:*?"<>|{}\x00-\x1f]' -or
        $Path.StartsWith('/') -or $Path.EndsWith('/') -or $invalidSegments.Count -gt 0) { throw "Invalid release path: $Path" }
    if ($Path -notin @('MorphocyteOS.exe', 'mihomo.exe', 'release-source.json', 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md') -and
        $Path -notmatch '^(Licenses|ThirdPartySource)/') { throw "Unexpected release file: $Path" }
    if ([System.IO.Path]::GetFileName($Path) -match '^(settings\.json|portable\.flag|.*\.log|.*\.ya?ml|.*\.bak.*|.*\.db)$') {
        throw "User state must not be included in Setup: $Path"
    }
}

Assert-NoLinkedPath -Path $package
Assert-NoLinkedPath -Path $output
$helperPath = [System.IO.Path]::GetFullPath($MaintenanceHelperPath)
Assert-NoLinkedPath -Path $helperPath
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf) -or
    (Get-Item -LiteralPath $helperPath).Length -le 0 -or
    (Get-Item -LiteralPath $helperPath).Length -gt 100MB) { throw 'Missing or oversized maintenance helper.' }
$helperHash = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    (Get-Item -LiteralPath $manifestPath).Length -gt 2MB) { throw 'Missing or oversized release manifest.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.SchemaVersion -ne 1 -or $manifest.Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$' -or
    @($manifest.Files).Count -lt 1 -or @($manifest.Files).Count -gt 4096) { throw 'Invalid release manifest.' }
$version = [string]$manifest.Version
$paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$manifestHashes = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$totalPayload = 0L
foreach ($file in $manifest.Files) {
    $relative = [string]$file.Path
    Assert-RelativeReleasePath -Path $relative
    if (-not $paths.Add($relative)) { throw "Duplicate manifest path: $relative" }
    if ($file.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.Size -isnot [ValueType] -or
        $file.Size -lt 0 -or $file.Size -gt 1GB -or [long]$file.Size -ne $file.Size) { throw "Invalid manifest entry: $relative" }
    $totalPayload += [long]$file.Size
    if ($totalPayload -gt 1GB) { throw 'Release payload is too large.' }
    $manifestHashes.Add($relative, ([string]$file.Sha256).ToLowerInvariant())
    $source = Join-Path $package $relative.Replace('/', '\')
    Assert-NoLinkedPath -Path $source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-Item -LiteralPath $source).Length -ne [long]$file.Size -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Manifest verification failed: $relative" }
}
foreach ($required in @('MorphocyteOS.exe', 'mihomo.exe', 'LICENSE.txt', 'README.md', 'THIRD-PARTY-NOTICES.md',
    'ThirdPartySource/Engine/sing-box.exe', 'release-source.json')) {
    if (-not $paths.Contains($required)) { throw "Required release file missing: $required" }
}
foreach ($entry in Get-ChildItem -LiteralPath $package -Recurse -Force) {
    if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked package entry.' }
    if ($entry.PSIsContainer) { continue }
    $relative = $entry.FullName.Substring($package.Length + 1).Replace('\', '/')
    if ($relative -ne 'release-manifest.json' -and -not $paths.Contains($relative)) { throw "Unlisted file in release package: $relative" }
}
$binaryVersion = (Get-Item -LiteralPath (Join-Path $package 'MorphocyteOS.exe')).VersionInfo.ProductVersion
if ($binaryVersion -ne $version) { throw "Binary version mismatch: $binaryVersion / $version" }

if (-not $PrepareOnly) {
    if (-not $IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
        foreach ($candidate in @('C:\Program Files\Inno Setup 7\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 7\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe')) {
            if (-not $IsccPath -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $IsccPath = $candidate }
        }
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        throw 'Inno Setup 7.1 or newer compiler was not found. Supply -IsccPath; this script never downloads or installs tools.'
    }
    $compilerVersion = (Get-Item -LiteralPath $IsccPath).VersionInfo.FileVersion
    # Official Inno Setup 7 binaries have 0.0.0.0 file resources; use its CLI.
    if ($compilerVersion -notmatch '^[1-9][0-9]*\.[0-9]+') {
        $probe = @(& $IsccPath --version 2>&1)
        $compilerVersion = @($probe | Where-Object { "$_" -match '^\d+\.\d+\.\d+$' } | Select-Object -First 1)[0]
    }
    if (-not $compilerVersion -or $compilerVersion -notmatch '^([0-9]+)\.([0-9]+)' -or
        [version]"$($Matches[1]).$($Matches[2])" -lt [version]'7.1') { throw 'Inno Setup 7.1 or newer is required for native dark styling and compile-time SHA-256 checks.' }
    $compilerMajor = [int]$Matches[1]
    & dotnet run --project (Join-Path $repo 'Source\InstallerArtworkBuilder\InstallerArtworkBuilder.csproj') -c Release -- --input (Join-Path $repo 'Source\MorphocyteRouter\Assets\morphocyte-icon.png') --outputdir (Join-Path $repo 'Installer\Assets')
    if ($LASTEXITCODE -ne 0) { throw 'Installer artwork generation failed.' }
}

$suffix = if ($TestInstaller) { '-Test' } else { '' }
if ($PreviewOnly) { $suffix += '-Preview'; if ($PreviewTheme -ne 'system') { $suffix += "-$PreviewTheme" } }
$outputName = "MorphocyteOS-$version-Setup$suffix"
$setupPath = Join-Path $output ($outputName + '.exe')
if (Test-Path -LiteralPath $setupPath) { throw 'Installer output already exists; choose a new output directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ('.installer-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$filesInclude = Join-Path $stage 'VerifiedFiles.iss'
$markerPath = Join-Path $stage '.installer-owned.json'
$appId = if ($TestInstaller) { '40B5D24D-0B16-48E6-85C5-6BE9C471D115' } else { '8AC863D3-AC74-44C3-90EC-082E8A9AA951' }
$marker = [ordered]@{ SchemaVersion = 1; AppId = $appId; TestMode = [bool]$TestInstaller }
[System.IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($relative in @($paths) + @('release-manifest.json')) {
    $source = Join-Path $package $relative.Replace('/', '\')
    $destination = [System.IO.Path]::GetDirectoryName($relative.Replace('/', '\'))
    $destination = if ($destination) { '{app}\' + $destination } else { '{app}' }
    $expectedHash = if ($relative -eq 'release-manifest.json') { $manifestHash } else { $manifestHashes[$relative] }
    $lines.Add(('Source: "{0}"; DestDir: "{1}"; Hash: "{2}"; Flags: ignoreversion uninsneveruninstall' -f $source, $destination, $expectedHash))
}
$markerHash = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$lines.Add(('Source: "{0}"; DestDir: "{{app}}"; Hash: "{1}"; Flags: ignoreversion uninsneveruninstall' -f $markerPath, $markerHash))
[System.IO.File]::WriteAllLines($filesInclude, $lines, [System.Text.UTF8Encoding]::new($true))
if ($PrepareOnly) {
    Write-Output ([pscustomobject]@{ StageDirectory = $stage; GeneratedFiles = $filesInclude; MaintenanceHelperPath = $helperPath; MaintenanceHelperHash = $helperHash; AppId = $appId; FileCount = $paths.Count; Version = $version })
    return
}
try {
    $defineOption = if ($compilerMajor -ge 7) { '--define=' } else { '/D' }
    $quietOption = if ($compilerMajor -ge 7) { '--quiet-progress' } else { '/Qp' }
    $compilerArgs = @($quietOption, "${defineOption}PackageDirectory=$package", "${defineOption}GeneratedFiles=$filesInclude",
        "${defineOption}MaintenanceHelperPath=$helperPath", "${defineOption}MaintenanceHelperHash=$helperHash", "${defineOption}AppVersion=$version",
        "${defineOption}PayloadManifestHash=$manifestHash",
        "${defineOption}OutputDirectory=$output", "${defineOption}InstallerOutputName=$outputName",
        "${defineOption}TestInstaller=$([int][bool]$TestInstaller)", "${defineOption}PreviewInstaller=$([int][bool]$PreviewOnly)",
        "${defineOption}PreviewTheme=$PreviewTheme", (Join-Path $repo 'Installer\MorphocyteOS.iss'))
    & $IsccPath @compilerArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Installer compilation failed.' }
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(($setupPath + '.sha256'), "$hash  $([System.IO.Path]::GetFileName($setupPath))`r`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Installer built: $setupPath"
    Write-Host 'No installation was started and no files were uploaded.'
    Write-Output ([pscustomobject]@{ OutputPath = $setupPath; HashPath = ($setupPath + '.sha256'); AppId = $appId; Version = $version })
}
finally {
    # Generated directory only: never remove or move release package files.
    $resolvedStage = [System.IO.Path]::GetFullPath($stage)
    if ([System.IO.Path]::GetDirectoryName($resolvedStage) -ne $output -or
        [System.IO.Path]::GetFileName($resolvedStage) -notmatch '^\.installer-build-[a-f0-9]{32}$') { throw 'Unsafe installer staging path.' }
    Assert-NoLinkedPath -Path $resolvedStage
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
