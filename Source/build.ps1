param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$SkipTests,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Container) -or
        @(Get-ChildItem -LiteralPath $OutputPath -Force).Count -gt 0) {
        throw 'Release output must be an empty directory. Use a new directory; no existing files were removed.'
    }
}
Set-Location -LiteralPath $PSScriptRoot
$coreSource = Join-Path $PSScriptRoot 'Packaging/Engine/mihomo.exe'
if (-not (Test-Path -LiteralPath $coreSource) -or (Get-FileHash -LiteralPath $coreSource -Algorithm SHA256).Hash -ne '4A2275F385FC11106F7D819C16B510FCCA5E4996B5E902D295BFC78AC29C5530') {
    throw 'Missing or modified source-built Mihomo executable in Packaging/Engine/mihomo.exe.'
}

function Run-Dotnet {
    param([string[]]$Arguments)
    if ($NoRestore) {
        $separator = [Array]::IndexOf($Arguments, '--')
        if ($separator -ge 0) {
            $Arguments = @($Arguments[0..($separator - 1)]) + '--no-restore' + @($Arguments[$separator..($Arguments.Length - 1)])
        }
        else { $Arguments += '--no-restore' }
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $Arguments" }
}

if (-not $SkipTests) {
    Run-Dotnet -Arguments @('run', '--project', 'SubscriptionImportTests/SubscriptionImportTests.csproj', '-c', 'Release', '-p:PublishSingleFile=false')
    Run-Dotnet -Arguments @('run', '--project', 'RouterRegressionTests/RouterRegressionTests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'UpdateServiceTests/UpdateServiceTests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'UpdaterTests/UpdaterTests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('build', 'FakeMihomo/FakeMihomo.csproj', '-c', 'Release')
    $fakeCorePath = Join-Path $PSScriptRoot 'FakeMihomo/bin/Release/net10.0/FakeMihomo.exe'
    Run-Dotnet -Arguments @('run', '--project', 'RouterIntegrationTests/RouterIntegrationTests.csproj', '-c', 'Release', '-p:PublishSingleFile=false', '--', $fakeCorePath)
    $wpfApplication = Join-Path $PSScriptRoot 'MorphocyteRouter/bin/Release/net10.0-windows/MorphocyteOS.dll'
    Run-Dotnet -Arguments @('run', '--project', 'UpdaterTests/UpdaterTests.csproj', '-c', 'Release', '--', '--verify-wpf-updater', $wpfApplication, (Get-Command dotnet).Source)
}

Run-Dotnet -Arguments @('publish', 'MorphocyteRouter/MorphocyteRouter.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', $OutputPath)
Copy-Item -LiteralPath $coreSource -Destination (Join-Path $OutputPath 'mihomo.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-source.json') -Destination (Join-Path $OutputPath 'release-source.json')
foreach ($name in @('README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputPath $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Packaging/LICENSE.txt') -Destination (Join-Path $OutputPath 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Packaging/THIRD-PARTY-NOTICES.md') -Destination (Join-Path $OutputPath 'THIRD-PARTY-NOTICES.md')
foreach ($name in @('Licenses', 'ThirdPartySource')) {
    $destination = Join-Path $OutputPath $name
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "Packaging/$name") | Copy-Item -Destination $destination -Recurse -Force
}
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MorphocyteRouter/MorphocyteRouter.csproj') -Raw
$manifestFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        Path = $_.FullName.Substring($OutputPath.Length + 1).Replace('\', '/')
        Size = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{ SchemaVersion = 1; Version = [string]$project.Project.PropertyGroup.Version; Files = $manifestFiles }
[System.IO.File]::WriteAllText((Join-Path $OutputPath 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
Write-Host "Built: $OutputPath"
