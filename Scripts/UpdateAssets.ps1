# Component archives retain ordinary release files and can be used from any installed version.
# A complete manifest is published separately; no client-specific base/delta is required.
function New-IncrementalUpdateAssets {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $ErrorActionPreference = 'Stop'
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid update version.' }
    $packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw 'Missing release package directory.' }
    if ($outputRoot.Equals($packageRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outputRoot.StartsWith($packageRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update assets must be outside the release package directory.'
    }
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    foreach ($checkedRoot in @($manifestPath, $outputRoot)) {
        for ($checkPath = $checkedRoot; $checkPath; $checkPath = [System.IO.Path]::GetDirectoryName($checkPath)) {
            if ((Test-Path -LiteralPath $checkPath) -and
                ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Linked release manifest or output directories are forbidden.'
            }
        }
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or (Get-Item -LiteralPath $manifestPath).Length -gt 2MB) {
        throw 'Missing or oversized release manifest.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Version -cne $ExpectedVersion -or
        @($manifest.Files).Count -lt 1 -or @($manifest.Files).Count -gt 4096) { throw 'Unsupported release manifest.' }

    $rootFiles = @('MorphocyteOS.exe', 'mihomo.exe', 'sing-box.exe', 'release-source.json', 'README.md', 'LICENSE.txt',
        'THIRD-PARTY-NOTICES.md', 'MorphocyteOS.dll', 'MorphocyteOS.deps.json', 'MorphocyteOS.runtimeconfig.json', 'YamlDotNet.dll')
    $records = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $orderedPaths = [System.Collections.Generic.List[string]]::new()
    [long]$totalBytes = 0
    foreach ($file in $manifest.Files) {
        $relative = [string]$file.Path
        if (-not $relative -or $relative.Length -gt 240 -or $relative.Contains('\') -or $relative.StartsWith('/') -or
            ($relative -notin $rootFiles -and $relative -notmatch '^(Licenses|ThirdPartySource)/')) { throw 'Unsafe update file path.' }
        foreach ($segment in $relative.Split('/')) {
            if (-not $segment -or $segment -in @('.', '..') -or $segment.EndsWith(' ') -or $segment.EndsWith('.') -or
                $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe update file name.' }
        }
        if ([System.IO.Path]::GetFileName($relative) -match '^(settings\.json|portable\.flag)$|\.ya?ml$|\.log$|\.bak') {
            throw 'Private state is forbidden in update assets.'
        }
        if ($records.ContainsKey($relative) -or $null -eq $file.Size -or [string]$file.Size -notmatch '^\d+$' -or $file.Size -lt 0 -or $file.Size -gt 1GB -or
            [string]$file.Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid update file record.' }
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not $fullPath.StartsWith($packageRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Update file escapes the release package.'
        }
        for ($checkPath = $fullPath; $checkPath; $checkPath = [System.IO.Path]::GetDirectoryName($checkPath)) {
            if ((Test-Path -LiteralPath $checkPath) -and
                ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Linked update files are forbidden.'
            }
            if ($checkPath.Equals($packageRoot, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf) -or (Get-Item -LiteralPath $fullPath).Length -ne [long]$file.Size -or
            (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ine [string]$file.Sha256) { throw 'Release file does not match its manifest.' }
        $totalBytes += [long]$file.Size
        if ($totalBytes -gt 1GB) { throw 'Update exceeds the expanded size limit.' }
        $records.Add($relative, $file)
        $orderedPaths.Add($relative)
    }
    if (-not $records.ContainsKey('MorphocyteOS.exe') -or -not $records.ContainsKey('mihomo.exe') -or
        -not $records.ContainsKey('release-source.json')) { throw 'Missing mandatory update file.' }
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force) {
        $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        if ($relative -cne 'release-manifest.json' -and -not $records.ContainsKey($relative)) { throw 'Unlisted file in release package.' }
    }
    $orderedPaths.Sort([System.StringComparer]::Ordinal)

    # Separately changed large binaries/source archives are downloaded independently.
    # Small notices and licenses share one compact archive instead of hundreds of HTTP requests.
    $groups = [System.Collections.Generic.List[object]]::new()
    $supportFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($relative in $orderedPaths) {
        if ($relative.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
            ([long]$records[$relative].Size -gt 1MB -and $relative -match '\.(zip|gz|tgz|7z)$')) {
            $groups.Add(@($relative))
        }
        else { $supportFiles.Add($relative) }
    }
    if ($supportFiles.Count -gt 0) { $groups.Add($supportFiles.ToArray()) }
    $indexPath = Join-Path $outputRoot "MorphocyteOS-$ExpectedVersion-update.json"
    if (Test-Path -LiteralPath $indexPath) { throw 'The update index already exists; existing assets will not be overwritten.' }
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packages = [System.Collections.Generic.List[object]]::new()
    $packagePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($group in $groups) {
        $temporaryPath = Join-Path $outputRoot ('.morphocyte-component-' + [guid]::NewGuid().ToString('N') + '.zip')
        try {
            $zip = [System.IO.Compression.ZipFile]::Open($temporaryPath, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($relative in $group) {
                    $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
                    $entry.ExternalAttributes = 0
                    $input = [System.IO.File]::OpenRead((Join-Path $packageRoot $relative))
                    try {
                        $entryStream = $entry.Open()
                        try { $input.CopyTo($entryStream) }
                        finally { $entryStream.Dispose() }
                    }
                    finally { $input.Dispose() }
                }
            }
            finally { $zip.Dispose() }
            # Verify archived bytes, not only the input metadata, before publishing their hash.
            $verificationZip = [System.IO.Compression.ZipFile]::OpenRead($temporaryPath)
            try {
                foreach ($entry in $verificationZip.Entries) {
                    $input = $entry.Open()
                    try {
                        $hasher = [System.Security.Cryptography.SHA256]::Create()
                        try { $entryHash = [System.BitConverter]::ToString($hasher.ComputeHash($input)).Replace('-', '').ToLowerInvariant() }
                        finally { $hasher.Dispose() }
                    }
                    finally { $input.Dispose() }
                    if ($entry.Length -ne [long]$records[$entry.FullName].Size -or $entryHash -ine [string]$records[$entry.FullName].Sha256) {
                        throw 'Archived update file does not match its manifest.'
                    }
                }
            }
            finally { $verificationZip.Dispose() }
            $hash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $name = "MorphocyteOS-part-$hash.zip"
            $destination = Join-Path $outputRoot $name
            if (Test-Path -LiteralPath $destination) { throw 'A component archive already exists; assets will not be overwritten.' }
            Move-Item -LiteralPath $temporaryPath -Destination $destination
            $packagePaths.Add($destination)
            $packages.Add([ordered]@{ Name = $name; Size = (Get-Item -LiteralPath $destination).Length; Sha256 = $hash; Files = @($group) })
        }
        finally {
            # Only the unique temporary file created above may be removed, never an existing asset.
            if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath -Force }
        }
    }
    $index = [ordered]@{ SchemaVersion = 1; Manifest = $manifest; Packages = $packages.ToArray() }
    $json = $index | ConvertTo-Json -Depth 8
    if ([System.Text.Encoding]::UTF8.GetByteCount($json) -gt 2MB) { throw 'Update index exceeds the client size limit.' }
    [System.IO.File]::WriteAllText($indexPath, $json, [System.Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ IndexPath = $indexPath; PackagePaths = $packagePaths.ToArray(); FileCount = $records.Count; ExpandedBytes = $totalBytes }
}
