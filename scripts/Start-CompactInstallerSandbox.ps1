<#
.SYNOPSIS
  ХОСТ: готовит стенд для проверки установщика Compact в Windows Sandbox и (только с -Launch) запускает его.
.DESCRIPTION
  Копирует в ASCII-папку установщик, манифест, квитанцию сборки, фикстуру ASR и гостевой скрипт
  Test-CompactInstallerInSandbox.ps1, создаёт .wsb (сеть, буфер обмена, аудио, принтеры отключены; входная папка
  только на чтение). Сам ничего не устанавливает на этом ПК. Результат гость пишет в <ResultsDir>\output\lifecycle.json.
.PARAMETER ArtifactDirectory
  Папка сборки с Slog-Setup-*-win-x64.exe, compact-installer.json и portable-stage.manifest.json.
.PARAMETER AsrAudio / AsrReference
  Фикстура для проверки распознавания внутри Sandbox (WAV и эталонный текст UTF-8).
#>
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$AsrAudio,
    [Parameter(Mandatory)][string]$AsrReference,
    [string]$ResultsDir = '',
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).ProviderPath
$installer = Get-ChildItem -LiteralPath $artifact -Filter 'Slog-Setup-*-win-x64.exe' -File | Select-Object -First 1
if (!$installer) { throw 'Installer not found in artifact directory.' }
foreach ($name in 'compact-installer.json', 'portable-stage.manifest.json') {
    if (!(Test-Path -LiteralPath (Join-Path $artifact $name))) { throw "Missing $name." }
}
$receipt = Get-Content -LiteralPath (Join-Path $artifact 'compact-installer.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $installer.FullName).Hash.ToLowerInvariant() -ne $receipt.sha256) { throw 'Installer hash differs from its receipt.' }
if (Get-Process -Name WindowsSandbox, WindowsSandboxServer, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is already running; close it first.' }

if (!$ResultsDir) { $ResultsDir = Join-Path $env:PUBLIC ('SlogInstallerSandbox\' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')) }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$results = (Resolve-Path -LiteralPath $ResultsDir).ProviderPath
if ($results -notmatch '^[\x20-\x7E]+$') { throw 'Results path must be ASCII.' }
$in = Join-Path $results 'input'
$out = Join-Path $results 'output'
New-Item -ItemType Directory -Force -Path $in, $out | Out-Null
Copy-Item -LiteralPath $installer.FullName -Destination $in
Copy-Item -LiteralPath (Join-Path $artifact 'compact-installer.json'), (Join-Path $artifact 'portable-stage.manifest.json') -Destination $in
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-CompactInstallerInSandbox.ps1') -Destination $in
Copy-Item -LiteralPath $AsrAudio -Destination (Join-Path $in 'synthetic-russian.wav')
Copy-Item -LiteralPath $AsrReference -Destination (Join-Path $in 'synthetic-reference.txt')

$wsb = Join-Path $results 'slog-installer.wsb'
@"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MappedFolders>
    <MappedFolder><HostFolder>$in</HostFolder><SandboxFolder>C:\VoiceInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\VoiceOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\VoiceInput\Test-CompactInstallerInSandbox.ps1 -Apply</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsb -Encoding utf8
[pscustomobject]@{ prepared = $true; wsb = $wsb; output = $out; installerSha256 = $receipt.sha256; launched = [bool]$Launch } | ConvertTo-Json
if ($Launch) { Start-Process -FilePath 'WindowsSandbox.exe' -ArgumentList ('"' + $wsb + '"') }
