param(
    [string]$GoExe = 'go',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Engine\mihomo.exe')
)

$ErrorActionPreference = 'Stop'
$archivePath = Join-Path $PSScriptRoot 'mihomo-v1.19.31-corresponding-source.tar.gz'
$expectedArchiveHash = 'F52777EFC150719141ECC8F30071CD5DFDAAA770E9E4AD19A6A4EC3CBD583D2B'
$expectedBinaryHash = '4A2275F385FC11106F7D819C16B510FCCA5E4996B5E902D295BFC78AC29C5530'

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

$sourcePath = Join-Path $workspace 'mihomo-ab405bad5beeeac8b003bb01f60f134f6df54471'
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
        & $GoExe mod download
        if ($LASTEXITCODE -ne 0) { throw "go mod download failed ($LASTEXITCODE)." }

        $ldflags = '-X github.com/metacubex/mihomo/constant.Version=v1.19.31 -X github.com/metacubex/mihomo/constant.BuildTime=2026-10-01T00:00:00Z -w -s -buildid='
        & $GoExe build -mod=mod -buildvcs=false -tags with_gvisor -trimpath -ldflags $ldflags -o $temporaryBinary .
        if ($LASTEXITCODE -ne 0) { throw "go build failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
}

$actualBinaryHash = (Get-FileHash -LiteralPath $temporaryBinary -Algorithm SHA256).Hash
if ($actualBinaryHash -ne $expectedBinaryHash) {
    throw "Build completed but binary hash differs. Expected $expectedBinaryHash, got $actualBinaryHash. The toolchain or build inputs may differ."
}

$destination = [System.IO.Path]::GetFullPath($OutputPath)
$destinationDirectory = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
Copy-Item -LiteralPath $temporaryBinary -Destination $destination -Force
Write-Output "Built and verified Mihomo v1.19.31: $destination"
Write-Output "SHA-256: $actualBinaryHash"
