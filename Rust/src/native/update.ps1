param(
    [Parameter(Mandatory)][ValidateSet('Stage','Apply')][string]$Mode,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Digest,
    [int]$ShellPid,
    [ValidateSet('resume','idle')][string]$Resume = 'idle'
)
$ErrorActionPreference = 'Stop'
$operation = Get-Item -LiteralPath $PSScriptRoot
$cache = $operation.Parent
$backend = $cache.Parent
if ($operation.Name -notmatch '^[a-fA-F0-9]{32}$' -or $cache.Name -ne '.atlas-updates' -or $backend.Name -ne 'backend') {
    throw 'Неверная папка установщика.'
}
$installation = $backend.Parent.FullName
$payload = Join-Path $operation.FullName 'payload'
$shell = Join-Path $installation 'MorphocyteAtlas.exe'
$required = @('MorphocyteAtlas.exe','WebView2Loader.dll','backend/release-source.json',
    'backend/ThirdPartySource/Engine/sing-box.exe','backend/ThirdPartySource/Engine/xray.exe')
$cloudPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Assert-ManagedPath([string]$Relative) {
    if ($Relative -cin @('MorphocyteAtlas.exe','WebView2Loader.dll','Запуск.txt','backend/release-source.json',
        'backend/LICENSE.txt','backend/THIRD-PARTY-NOTICES.md')) { return }
    if ($Relative -cnotmatch '^backend/(Licenses|ThirdPartySource)/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$') {
        throw 'В архиве есть неподдерживаемый путь.'
    }
    foreach ($part in $Relative.Split('/')) {
        if ($part -in '.', '..' -or $part.EndsWith('.') -or
            $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
            throw 'В архиве есть недопустимое имя файла.'
        }
    }
}

function Resolve-UpdatePath([string]$Root, [string]$Relative) {
    $full = Join-Path $Root $Relative
    for ($path = $full; $path; $path = [IO.Path]::GetDirectoryName($path)) {
        if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
            if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) {
                if (!$cloudPaths.Contains($path)) {
                    $tag = & "$env:SystemRoot\System32\fsutil.exe" reparsepoint query $path
                    if ($LASTEXITCODE -ne 0 -or !($tag | Select-String -Quiet -Pattern '\b0x9000[0-9a-f]01a\b')) {
                        throw 'Папка обновления или назначения находится по ссылке.'
                    }
                    [void]$cloudPaths.Add($path)
                }
            }
        }
    }
    return $full
}

function Assert-Required($Files) {
    foreach ($name in $required) {
        if (!$Files.Contains($name)) { throw "В архиве отсутствует $name." }
    }
}

if ($Mode -eq 'Stage') {
    $archive = Resolve-UpdatePath $operation.FullName 'package.zip'
    $stream = [IO.File]::Open($archive, 'Open', 'Read', 'Read')
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
        finally { $hasher.Dispose() }
        if ($actual -ne $Digest) { throw 'Контрольная сумма ZIP не совпадает.' }
        $stream.Position = 0
        Add-Type -AssemblyName System.IO.Compression
        $zip = [IO.Compression.ZipArchive]::new($stream, 'Read', $true)
        try {
            if ($zip.Entries.Count -gt 8192) { throw 'Слишком много файлов в архиве.' }
            $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $total = 0L
            $prefix = 'MorphocyteAtlas/'
            $buffer = New-Object byte[] 65536
            foreach ($entry in $zip.Entries) {
                if (!$entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or
                    (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Неверная структура ZIP.' }
                $name = $entry.FullName.Substring($prefix.Length)
                Assert-ManagedPath $name
                $total += $entry.Length
                if (!$files.Add($name) -or $total -gt 1GB) { throw 'Архив содержит дубликаты или слишком большой.' }
                $target = Resolve-UpdatePath $payload $name
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                $inputStream = $entry.Open()
                try {
                    $output = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
                    try {
                        while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            if ($output.Position + $count -gt $entry.Length) { throw 'Неверный размер файла в ZIP.' }
                            $output.Write($buffer, 0, $count)
                        }
                        if ($output.Position -ne $entry.Length) { throw 'ZIP повреждён.' }
                    } finally { $output.Dispose() }
                } finally { $inputStream.Dispose() }
            }
            Assert-Required $files
            $source = Get-Content -Raw -LiteralPath (Join-Path $payload 'backend/release-source.json') | ConvertFrom-Json
            if ($source.githubRepository -cne 'Rabotaet3/MorphocyteOS') { throw 'Архив предназначен для другого приложения.' }
            [IO.File]::WriteAllLines((Join-Path $operation.FullName 'files.txt'), [string[]]@($files))
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    exit 0
}

$app = [Diagnostics.Process]::GetProcessById($ShellPid)
try {
    if ($app.MainModule.FileName -ne $shell) { throw 'Установщик не нашёл экземпляр Морфоцита.' }
    $backup = Join-Path $operation.FullName 'backup'
    $paths = foreach ($name in [IO.File]::ReadAllLines((Join-Path $operation.FullName 'files.txt'))) {
        Assert-ManagedPath $name
        [pscustomobject]@{
            Source = Resolve-UpdatePath $payload $name
            Target = Resolve-UpdatePath $installation $name
            Backup = Resolve-UpdatePath $backup $name
        }
    }
    $lock = [IO.File]::Open((Resolve-UpdatePath $cache.FullName 'install.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        [IO.File]::WriteAllText((Join-Path $operation.FullName 'ready'), '')
        if (!$app.WaitForExit(70000) -or ![IO.File]::Exists((Join-Path $operation.FullName 'commit'))) { exit 1 }
        $applied = [Collections.Generic.Stack[object]]::new()
        $message = 'Обновление Морфоцита установлено.'
        $cleanup = $true
        try {
            foreach ($file in $paths) {
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Target))
                $exists = [IO.File]::Exists($file.Target)
                if ($exists) {
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Backup))
                    [IO.File]::Replace($file.Source, $file.Target, $file.Backup)
                } else { [IO.File]::Move($file.Source, $file.Target) }
                $applied.Push($file)
            }
        } catch {
            $message = 'Не удалось установить обновление. Предыдущая сборка восстановлена.'
            while ($applied.Count) {
                $file = $applied.Pop()
                try {
                    if ([IO.File]::Exists($file.Backup)) { [IO.File]::Replace($file.Backup, $file.Target, [NullString]::Value) }
                    else { [IO.File]::Delete($file.Target) }
                } catch { $cleanup = $false }
            }
            if (!$cleanup) { $message = "Не удалось восстановить часть файлов. Резервная копия в $backup" }
        }
        $result = @{ operation=$operation.Name; message=$message; cleanup=$cleanup } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText((Join-Path $cache.FullName 'result.json'), $result)
    } finally { $lock.Dispose() }
    if ($Resume -eq 'resume') { Start-Process -FilePath $shell -WorkingDirectory $installation -ArgumentList '--connect-admin' }
    else { Start-Process -FilePath $shell -WorkingDirectory $installation }
} finally { $app.Dispose() }
