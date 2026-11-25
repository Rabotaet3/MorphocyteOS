[CmdletBinding(DefaultParameterSetName = 'Native')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Native')][string]$EngineAssetsDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Legacy')][string]$Release136Directory,
    [ValidatePattern('^dist(?:-native)?(?:-[0-9]+\.[0-9]+\.[0-9]+)?$')][string]$OutputDirectory,
    [switch]$Offline,
    [switch]$PortableArchive
)

$ErrorActionPreference = 'Stop'
$native = $PSCmdlet.ParameterSetName -eq 'Native'
if (!$PSBoundParameters.ContainsKey('OutputDirectory')) {
    $version = (Select-String -LiteralPath (Join-Path $PSScriptRoot 'Cargo.toml') -Pattern '^version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"' |
        Select-Object -First 1).Matches.Groups[1].Value
    $OutputDirectory = if ($native) { "dist-native-$version" } else { "dist-$version" }
}
if ($native -ne ($OutputDirectory -match '^dist-native-[0-9]+\.[0-9]+\.[0-9]+$')) {
    throw 'Для самостоятельной тестовой сборки используй отдельную папку dist-native-<версия>.'
}
$assets = if ($native) { $EngineAssetsDirectory } else { $Release136Directory }
$assets = (Resolve-Path -LiteralPath $assets).Path
foreach ($name in 'sing-box.exe', 'xray.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $assets "ThirdPartySource\Engine\$name") -PathType Leaf)) {
        throw "В папке ресурсов отсутствует ядро $name."
    }
}
$previousCargoOffline = $env:CARGO_NET_OFFLINE
if ($Offline) { $env:CARGO_NET_OFFLINE = 'true' }
Push-Location -LiteralPath $PSScriptRoot
try {
    $destination = Join-Path $PSScriptRoot $OutputDirectory
    $backend = Join-Path $destination 'backend'
    $source = Join-Path $PSScriptRoot 'Backend\Source'
    & python .\build-ui.py
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать интерфейс.' }
    if (!$native) {
        $engine = Join-Path $source 'Packaging\Engine'
        New-Item -ItemType Directory -Force -Path $engine | Out-Null
        Copy-Item -LiteralPath (Join-Path $assets 'ThirdPartySource\Engine\xray.exe') -Destination $engine -Force
        $restore = if ($Offline) { @('--no-restore', '-p:NuGetAudit=false') } else { @() }
        & dotnet publish (Join-Path $source 'MorphocyteRouter\MorphocyteRouter.csproj') -c Release -r win-x64 --self-contained true -o $backend @restore
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать модуль 1.3.6.' }
    } elseif (Test-Path -LiteralPath (Join-Path $backend 'MorphocyteOS.exe')) {
        throw 'В выбранной папке уже находится модуль 1.3.6. Укажи отдельную папку для Rust-сборки.'
    }
    if ($assets -ne $backend) {
        $excludedAssets = if ($native) { 'mihomo|^(?:DotNet-|YamlDotNet-)' } else { 'mihomo' }
        foreach ($directory in 'ThirdPartySource', 'Licenses') {
            $output = Join-Path $backend $directory
            New-Item -ItemType Directory -Force -Path $output | Out-Null
            Get-ChildItem -LiteralPath (Join-Path $assets $directory) | Where-Object {
                $_.Name -notmatch $excludedAssets -and !($native -and $_.Name -match '^(sing-box-v[0-9.]+)\.tar\.gz$' -and
                    (Test-Path -LiteralPath (Join-Path $_.DirectoryName ($Matches[1] + '-corresponding-source.tar.gz')) -PathType Leaf))
            } |
                Copy-Item -Destination $output -Recurse -Force
        }
        foreach ($name in 'THIRD-PARTY-NOTICES.md','LICENSE.txt','release-source.json') {
            Copy-Item -LiteralPath (Join-Path $assets $name) -Destination $backend -Force
        }
    }
    if ($native) {
        foreach ($name in 'THIRD-PARTY-NOTICES.md', 'ThirdPartySource/README.md') {
            $noticePath = Join-Path $backend $name
            $notice = Get-Content -Raw -LiteralPath $noticePath
            $notice = $notice -replace '(?ms)^## (?:Mihomo|YamlDotNet|\.NET)[^\r\n]*\r?\n.*?(?=^## |\z)', ''
            $snapshot = [regex]::Match($notice, '(?m)^- Исходный snapshot — (sing-box-v[0-9.]+)\.tar\.gz\.\r?\n- SHA-256 snapshot — [0-9a-f]+\.?\r?\n')
            if ($snapshot.Success) {
                $archiveStem = Join-Path (Join-Path $backend 'ThirdPartySource') $snapshot.Groups[1].Value
                if ((Test-Path -LiteralPath ($archiveStem + '-corresponding-source.tar.gz')) -and
                    !(Test-Path -LiteralPath ($archiveStem + '.tar.gz'))) {
                    $notice = $notice.Replace($snapshot.Value,'')
                }
            }
            $notice = $notice -replace ' Mihomo распространяется отдельно[^\r\n]*', ''
            Set-Content -LiteralPath $noticePath -Value $notice -Encoding utf8 -NoNewline
        }
    }
    if (!$native) {
        New-Item -ItemType File -Path (Join-Path $backend 'portable.flag') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $backend 'atlas-module.flag') -Force | Out-Null
    }
    Copy-Item -LiteralPath .\ui\flags\LICENSE -Destination (Join-Path $backend 'Licenses\FlagIcons-MIT.txt') -Force

    $localRust = Join-Path $PSScriptRoot '.tools\cargo\bin\cargo.exe'
    $llvm = Join-Path $PSScriptRoot '.tools\llvm-bin'
    $features = if (!$native) { @('--no-default-features') } else { @() }
    if ((Test-Path -LiteralPath $localRust) -and (Test-Path -LiteralPath (Join-Path $llvm 'llvm-dlltool.exe'))) {
        $env:CARGO_HOME = Join-Path $PSScriptRoot '.tools\cargo'
        $env:RUSTUP_HOME = Join-Path $PSScriptRoot '.tools\rustup'
        $rustlib = Join-Path $env:RUSTUP_HOME 'toolchains\stable-x86_64-pc-windows-gnu\lib\rustlib\x86_64-pc-windows-gnu\bin'
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = Join-Path $rustlib 'self-contained\ld.exe'
        $env:ATLAS_LLVM_BIN = $llvm
        $env:PATH = "$llvm;$env:PATH"
        $env:RUSTFLAGS = '-C linker-flavor=ld -C link-self-contained=yes -C dlltool=llvm-dlltool.exe'
        $cargo = $localRust
        $lld = Join-Path $llvm 'ld.lld.exe'
        Copy-Item -LiteralPath (Join-Path $rustlib 'rust-lld.exe') -Destination $lld -Force
        & $cargo rustc --release --locked --bin morphocyte-atlas @features -- -C "linker=$lld" -C linker-flavor=gnu-lld
    } else {
        $cargo = (Get-Command cargo.exe -ErrorAction Stop).Source
        & $cargo build --release --locked @features
    }
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать Rust-окно.' }
    Copy-Item -LiteralPath .\target\release\morphocyte-atlas.exe -Destination (Join-Path $destination 'MorphocyteAtlas.exe') -Force
    $loader = Get-ChildItem -LiteralPath .\target\release\build -Recurse -Filter WebView2Loader.dll |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName | Select-Object -First 1
    if ($loader) { Copy-Item -LiteralPath $loader.FullName -Destination $destination -Force }
    if ($PortableArchive) {
        & python .\package-portable.py $destination
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать переносимый архив.' }
    }
} finally {
    $env:CARGO_NET_OFFLINE = $previousCargoOffline
    Pop-Location
}
