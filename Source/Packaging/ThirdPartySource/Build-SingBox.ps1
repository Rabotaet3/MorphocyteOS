param([string]$GoExe = 'go', [string]$OutputPath = (Join-Path $PSScriptRoot 'sing-box-rebuilt.exe'))
$ErrorActionPreference = 'Stop'
$archive = Join-Path $PSScriptRoot 'sing-box-v1.14.2-corresponding-source.tar.gz'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'C2B3D9AE082A364AA082E120931B1DF44E68E63AA27C7CA51201B4AE19660D75') { throw 'Source archive hash mismatch.' }
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('MorphocyteOS-SingBox-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
tar -xzf $archive -C $workspace
if ($LASTEXITCODE -ne 0) { throw 'Cannot unpack source archive.' }
$names = @('GOOS','GOARCH','GOAMD64','CGO_ENABLED','GOTOOLCHAIN')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:GOAMD64 = 'v1'; $env:CGO_ENABLED = '0'; $env:GOTOOLCHAIN = 'local'
    Push-Location (Join-Path $workspace 'sing-box-1.14.2')
    try {
        $tags = 'with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_acme,with_clash_api,with_tailscale,with_ccm,with_ocm,with_cloudflared,with_naive_outbound,with_purego,with_usbip,with_openvpn,with_openconnect,badlinkname,tfogo_checklinkname0'
        $binary = Join-Path $workspace 'sing-box.exe'
        & $GoExe build -mod=vendor -buildvcs=false -trimpath -tags $tags -ldflags '-checklinkname=0 -s -w -X github.com/sagernet/sing-box/constant.Version=1.14.2' -o $binary ./cmd/sing-box
        if ($LASTEXITCODE -ne 0) { throw 'Windows sing-box rebuild failed.' }
    } finally { Pop-Location }
} finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') } }
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Existing output is not overwritten.' }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $destination
Write-Output "Windows rebuild completed: $destination"
Write-Output 'This is not a promise of bit-identical reproduction of the official release binary.'
