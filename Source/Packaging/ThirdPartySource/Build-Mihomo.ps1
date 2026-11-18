param(
    [string]$GoExe = 'go',
    [string]$OutputPath = (Join-Path $PSScriptRoot 'mihomo-rebuilt.exe')
)

$ErrorActionPreference = 'Stop'
$archivePath = Join-Path $PSScriptRoot 'mihomo-v1.19.34-corresponding-source.tar.gz'
$expectedArchiveHash = '02D9C3FBCD456677C35129DF1AEEF2C429D09DC9D14CFB2332AF6AE204354C57'

if (-not (Test-Path -LiteralPath $archivePath)) { throw "Missing source archive: $archivePath" }
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedArchiveHash) {
    throw 'The corresponding-source archive hash does not match the release metadata.'
}

$goCommand = Get-Command -Name $GoExe -ErrorAction SilentlyContinue
if (-not $goCommand -and -not (Test-Path -LiteralPath $GoExe)) { throw "Go executable not found: $GoExe" }
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ('MorphocyteOS-Mihomo-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
tar -xzf $archivePath -C $workspace
if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the corresponding-source archive.' }

$sourcePath = Join-Path $workspace 'mihomo-0455d0d28c4526937380505153e65a843d8612e8'
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'go.mod'))) { throw 'The source archive layout is unexpected.' }
$temporaryBinary = Join-Path $workspace 'mihomo.exe'
$environmentNames = @('GOOS', 'GOARCH', 'GOAMD64', 'CGO_ENABLED', 'GOTOOLCHAIN')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

try {
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:GOAMD64 = 'v1'
    $env:CGO_ENABLED = '0'
    $env:GOTOOLCHAIN = 'local'
    Push-Location $sourcePath
    try {
        $ldflags = '-X github.com/metacubex/mihomo/constant.Version=v1.19.34 -X github.com/metacubex/mihomo/constant.BuildTime=2026-11-11T06:48:41Z -w -s -buildid='
        & $GoExe build -mod=vendor -buildvcs=false -tags with_gvisor -trimpath -ldflags $ldflags -o $temporaryBinary .
        if ($LASTEXITCODE -ne 0) { throw "go build failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
}

$actualBinaryHash = (Get-FileHash -LiteralPath $temporaryBinary -Algorithm SHA256).Hash

$destination = [System.IO.Path]::GetFullPath($OutputPath)
$destinationDirectory = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
Copy-Item -LiteralPath $temporaryBinary -Destination $destination -Force
Write-Output "Windows rebuild completed for Mihomo v1.19.34: $destination"
Write-Output "SHA-256: $actualBinaryHash"
Write-Output 'The official release binary is supplied separately. Rebuild hashes depend on the Go toolchain.'
