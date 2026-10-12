param([string]$FixtureRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateAssets.ps1')
if (-not $FixtureRoot) { $FixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('Morphocyte-update-assets-' + [guid]::NewGuid().ToString('N')) }
$FixtureRoot = [System.IO.Path]::GetFullPath($FixtureRoot)
if (Test-Path -LiteralPath $FixtureRoot) { throw 'Test fixture directory must not exist.' }
$package = Join-Path $FixtureRoot 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$paths = @('MorphocyteOS.exe', 'mihomo.exe', 'ThirdPartySource/Engine/sing-box.exe', 'release-source.json', 'README.md',
    'LICENSE.txt', 'Licenses/example/LICENSE', 'ThirdPartySource/example-source.tar.gz')
foreach ($relative in $paths) {
    $target = Join-Path $package $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    if ($relative -eq 'ThirdPartySource/example-source.tar.gz') {
        [System.IO.File]::WriteAllBytes($target, [byte[]]::new(1MB + 1))
    }
    else { [System.IO.File]::WriteAllText($target, ('fixture: ' + $relative), [System.Text.UTF8Encoding]::new($false)) }
}
function Write-FixtureManifest {
    $files = @(foreach ($relative in $paths) {
        $target = Join-Path $package $relative
        [ordered]@{ Path = $relative; Size = (Get-Item -LiteralPath $target).Length;
            Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [System.IO.File]::WriteAllText((Join-Path $package 'release-manifest.json'),
        ([ordered]@{ SchemaVersion = 1; Version = '9.8.7'; Files = $files } | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
}
function Assert-Test([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Test $rejected $Message
}
Write-FixtureManifest
$first = New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'first') -ExpectedVersion '9.8.7'
$index = Get-Content -LiteralPath $first.IndexPath -Raw | ConvertFrom-Json
Assert-Test ($index.SchemaVersion -eq 1 -and $index.Manifest.Version -eq '9.8.7') 'Incorrect update index contract.'
Assert-Test ($index.Packages.Count -eq 5) 'Expected three engines/app, one source archive, one support component.'
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($component in $index.Packages) {
    $zipPath = Join-Path (Split-Path -Parent $first.IndexPath) $component.Name
    Assert-Test ($component.Name -ceq ('MorphocyteOS-part-' + $component.Sha256 + '.zip')) 'Incorrect component name.'
    Assert-Test ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ieq $component.Sha256) 'Incorrect component hash.'
    Assert-Test ((Get-Item -LiteralPath $zipPath).Length -eq $component.Size) 'Incorrect component size.'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        Assert-Test ($zip.Entries.Count -eq $component.Files.Count) 'Unexpected component file count.'
        foreach ($entry in $zip.Entries) {
            Assert-Test ($entry.FullName -cin $component.Files) 'Unlisted archive entry.'
            Assert-Test ($seen.Add($entry.FullName)) 'Duplicate component file.'
            Assert-Test ($entry.LastWriteTime.Year -eq 1980 -and $entry.LastWriteTime.Month -eq 1 -and $entry.LastWriteTime.Day -eq 1) 'Unstable timestamp.'
            $input = $entry.Open()
            try {
                $hasher = [System.Security.Cryptography.SHA256]::Create()
                try { $entryHash = [System.BitConverter]::ToString($hasher.ComputeHash($input)).Replace('-', '').ToLowerInvariant() }
                finally { $hasher.Dispose() }
            }
            finally { $input.Dispose() }
            $record = @($index.Manifest.Files | Where-Object { $_.Path -ceq $entry.FullName })[0]
            Assert-Test ($record.Sha256 -ceq $entryHash -and $record.Size -eq $entry.Length) 'Extracted file does not match manifest.'
        }
    }
    finally { $zip.Dispose() }
}
Assert-Test ($seen.Count -eq $paths.Count -and -not $seen.Contains('release-manifest.json')) 'Incomplete component coverage.'
# Timestamps and input enumeration order must not invalidate otherwise unchanged components.
foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File) { $file.LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-14) }
$paths = @($paths | Sort-Object -Descending)
Write-FixtureManifest
$second = New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'second') -ExpectedVersion '9.8.7'
$secondIndex = Get-Content -LiteralPath $second.IndexPath -Raw | ConvertFrom-Json
Assert-Test (($index.Packages.Name -join ',') -ceq ($secondIndex.Packages.Name -join ',')) 'Components are not reproducible.'
# Changing the application must leave engines, sources, and support components reusable.
[System.IO.File]::AppendAllText((Join-Path $package 'MorphocyteOS.exe'), ' updated', [System.Text.UTF8Encoding]::new($false))
Write-FixtureManifest
$third = New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'third') -ExpectedVersion '9.8.7'
$thirdIndex = Get-Content -LiteralPath $third.IndexPath -Raw | ConvertFrom-Json
Assert-Test (@($index.Packages.Name | Where-Object { $_ -notin $thirdIndex.Packages.Name }).Count -eq 1) 'An app-only change invalidates unrelated components.'
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'third') -ExpectedVersion '9.8.7' } 'Existing update assets were overwritten.'
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $package 'assets') -ExpectedVersion '9.8.7' } 'Nested asset output accepted.'
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'wrong-version') -ExpectedVersion '9.8.8' } 'Wrong version accepted.'
[System.IO.File]::WriteAllText((Join-Path $package 'settings.json'), '{"fixture":true}')
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'private-state') -ExpectedVersion '9.8.7' } 'Private state accepted.'
Remove-Item -LiteralPath (Join-Path $package 'settings.json')
[System.IO.File]::AppendAllText((Join-Path $package 'README.md'), ' changed')
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'bad-hash') -ExpectedVersion '9.8.7' } 'Changed file accepted without a manifest update.'
Write-FixtureManifest
$manifestPath = Join-Path $package 'release-manifest.json'
$safeManifestText = Get-Content -LiteralPath $manifestPath -Raw
$unsafeManifest = $safeManifestText | ConvertFrom-Json
$unsafeManifest.Files[0].Path = '../README.md'
[System.IO.File]::WriteAllText($manifestPath, ($unsafeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'bad-path') -ExpectedVersion '9.8.7' } 'Escaping path accepted.'
$unsafeManifest = $safeManifestText | ConvertFrom-Json
$unsafeManifest.Files = @($unsafeManifest.Files) + @($unsafeManifest.Files[0])
[System.IO.File]::WriteAllText($manifestPath, ($unsafeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'duplicate-path') -ExpectedVersion '9.8.7' } 'Duplicate path accepted.'
$unsafeManifest = $safeManifestText | ConvertFrom-Json
$unsafeManifest.Files[0].Size = 0.25
[System.IO.File]::WriteAllText($manifestPath, ($unsafeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Assert-Rejected { New-IncrementalUpdateAssets -PackageDirectory $package -OutputDirectory (Join-Path $FixtureRoot 'fractional-size') -ExpectedVersion '9.8.7' } 'Fractional size accepted.'
[System.IO.File]::WriteAllText($manifestPath, $safeManifestText, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Incremental packaging checks passed: coverage, compression groups, checksums, deterministic archives, reuse, overwrite and private-state protection.'
Write-Host "Test fixtures: $FixtureRoot"
