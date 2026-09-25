param(
    [ValidateRange(1, 65535)][int]$Port = 8085,
    [ValidatePattern('^[D-Z]$')][string]$Drive = 'W'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    throw 'Run this script with Windows PowerShell (powershell.exe), because PowerShell 7 has a known WebDAV directory enumeration issue.'
}
$driveName = $Drive + ':'
$driveRoot = $driveName + '\'
if (Test-Path -LiteralPath $driveRoot) { throw "$driveName is already in use." }
# A disconnected persistent mapping must not be overwritten either.
$ErrorActionPreference = 'Continue'
$existing = & net.exe use $driveName 2>$null
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -eq 0) { throw "$driveName already has a network mapping." }
$folderName = 'webdav-smoke-' + [guid]::NewGuid().ToString('N')
$testRoot = $driveRoot + $folderName
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) $folderName
$mapped = $false
$preferencePath = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Network\Persistent Connections'
$savedPreference = (Get-ItemProperty -LiteralPath $preferencePath -ErrorAction SilentlyContinue).SaveConnections
if ($savedPreference -notin @('yes', 'no')) { $savedPreference = 'yes' }
function Connect-TestDrive {
    & net.exe use $driveName "http://localhost:$Port/" /persistent:no
    if ($LASTEXITCODE -ne 0) { throw "Mapping failed with exit code $LASTEXITCODE. Check the server and WebClient service." }
}
try {
    Connect-TestDrive
    $mapped = $true
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $tempRoot 'input.txt') -Value 'first' -NoNewline
    Copy-Item -LiteralPath (Join-Path $tempRoot 'input.txt') -Destination (Join-Path $testRoot 'hello.txt')
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'hello.txt') -Raw) -ne 'first') { throw 'Copy-in/read failed.' }
    Set-Content -LiteralPath (Join-Path $testRoot 'hello.txt') -Value 'edited' -NoNewline
    Copy-Item -LiteralPath (Join-Path $testRoot 'hello.txt') -Destination (Join-Path $tempRoot 'output.txt')
    if ((Get-Content -LiteralPath (Join-Path $tempRoot 'output.txt') -Raw) -ne 'edited') { throw 'Edit/copy-out failed.' }
    Copy-Item -LiteralPath (Join-Path $testRoot 'hello.txt') -Destination (Join-Path $testRoot 'copy.txt')
    Rename-Item -LiteralPath (Join-Path $testRoot 'copy.txt') -NewName 'renamed.txt'
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'nested') | Out-Null
    $moveSource = [IO.Path]::GetFullPath((Join-Path $testRoot 'renamed.txt'))
    $moveTarget = [IO.Path]::GetFullPath((Join-Path $testRoot 'nested\renamed.txt'))
    if (!$moveSource.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        !$moveTarget.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Move path escaped test root.' }
    Move-Item -LiteralPath $moveSource -Destination $moveTarget
    Set-Content -LiteralPath (Join-Path $testRoot 'save.tmp') -Value 'saved' -NoNewline
    Remove-Item -LiteralPath (Join-Path $testRoot 'hello.txt')
    Rename-Item -LiteralPath (Join-Path $testRoot 'save.tmp') -NewName 'hello.txt'
    $entries = @(Get-ChildItem -LiteralPath $testRoot -Recurse)
    if ($entries.Count -ne 3) { throw "Expected 3 entries; found $($entries.Count)." }
    & net.exe use $driveName /delete /y
    if ($LASTEXITCODE -ne 0) { throw 'Disconnect failed.' }
    $mapped = $false
    Connect-TestDrive
    $mapped = $true
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'hello.txt') -Raw) -ne 'saved') { throw 'Reconnect/save verification failed.' }
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'nested\renamed.txt') -Raw) -ne 'edited') { throw 'Move verification failed.' }
    # Verify exact, generated deletion roots before recursive cleanup.
    if ([IO.Path]::GetFullPath($testRoot) -ne ($driveRoot + $folderName)) { throw 'Unexpected test root.' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
    if (Test-Path -LiteralPath $testRoot) { throw 'Delete verification failed.' }
    Write-Output 'PASS: map, enumerate, copy in/out, read, edit, copy, rename, move, temporary-file save, disconnect/reconnect, recursive delete.'
}
finally {
    try {
        if ($mapped -and (Test-Path -LiteralPath $testRoot) -and [IO.Path]::GetFullPath($testRoot) -eq ($driveRoot + $folderName)) {
                Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
    finally {
        if ($mapped) { & net.exe use $driveName /delete /y }
        # net use /persistent:no also changes the default for future mappings.
        & net.exe use "/persistent:$savedPreference" | Out-Null
        $expectedTemp = Join-Path ([IO.Path]::GetTempPath()) $folderName
        if ((Test-Path -LiteralPath $tempRoot) -and [IO.Path]::GetFullPath($tempRoot) -eq [IO.Path]::GetFullPath($expectedTemp)) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}
