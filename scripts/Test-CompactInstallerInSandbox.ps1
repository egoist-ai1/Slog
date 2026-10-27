[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$Apply) { [pscustomobject]@{planOnly=$true;scope='Windows Sandbox only';steps='Install, hash verify, native ASR, repair, uninstall, preserve Data'} | ConvertTo-Json; exit 0 }
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $PSScriptRoot -ne 'C:\VoiceInput') { throw 'Installer lifecycle is restricted to the isolated Windows Sandbox guest.' }
$resultPath = 'C:\VoiceOutput\lifecycle.json'
$steps = [System.Collections.Generic.List[string]]::new()
function Run-Native([string]$Path, [string[]]$Arguments, [int]$TimeoutMs=120000) {
    $child = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    $childHandle = $child.Handle
    if (!$child.WaitForExit($TimeoutMs)) { throw 'Guest child exceeded its time limit.' }
    $child.WaitForExit()
    $child.Refresh()
    if ($child.ExitCode -ne 0) { throw ('Guest child exit: ' + $child.ExitCode) }
}
try {
    $receipt = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'compact-installer.json') -Raw | ConvertFrom-Json
    $installer = Join-Path $PSScriptRoot ([IO.Path]::GetFileName($receipt.installer))
    if ((Get-FileHash -LiteralPath $installer).Hash -ne $receipt.sha256) { throw 'Guest installer hash mismatch.' }
    $payload = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'portable-stage.manifest.json') -Raw | ConvertFrom-Json
    $destination = Join-Path $env:LOCALAPPDATA 'Programs\Voice проверка\Compact'
    Run-Native $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$destination+'"'),'/LOG="C:\VoiceOutput\install.log"')
    foreach ($file in $payload.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $destination $file.path)).Hash -ne $file.sha256) { throw 'Installed payload differs from build manifest.' }
    }
    $steps.Add('Install and all payload hashes verified')
    $exe = Join-Path $destination 'Egoist.Voice.exe'
    Run-Native $exe @('--local-asr-check','C:\VoiceInput\synthetic-russian.wav','C:\VoiceInput\synthetic-reference.txt','C:\VoiceOutput\asr.json')
    if (!(Get-Content -LiteralPath 'C:\VoiceOutput\asr.json' -Raw | ConvertFrom-Json).passed) { throw 'Guest native ASR failed.' }
    $steps.Add('Offline CPU transcription without .NET installation verified')
    # Чистая Windows без .NET: окно настроек и капсула должны подняться и отрисоваться.
    Run-Native $exe @('--render-settings-preview','C:\VoiceOutput\settings-general.png','general')
    Run-Native $exe @('--render-state-preview','tail-half','C:\VoiceOutput\capsule-tail-half.png')
    foreach ($shot in 'settings-general.png','capsule-tail-half.png') {
        if (!(Test-Path -LiteralPath (Join-Path 'C:\VoiceOutput' $shot)) -or (Get-Item -LiteralPath (Join-Path 'C:\VoiceOutput' $shot)).Length -lt 2000) { throw ('UI render failed: ' + $shot) }
    }
    $steps.Add('Settings window and capsule render on clean Windows')
    Run-Native $exe @('--local-history-check','C:\VoiceInput\synthetic-russian.wav','C:\VoiceInput\synthetic-reference.txt','C:\VoiceOutput\history.json')
    if (!(Get-Content -LiteralPath 'C:\VoiceOutput\history.json' -Raw | ConvertFrom-Json).passed) { throw 'Guest AAC recovery failed.' }
    $steps.Add('AAC history recovery verified')
    $data = Join-Path $destination 'Data'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $sentinel = Join-Path $data 'user-preservation-check.txt'
    [IO.File]::WriteAllText($sentinel, 'Synthetic user data must survive repair and uninstall')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
    Run-Native $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$destination+'"'),'/LOG="C:\VoiceOutput\repair.log"')
    if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Repair modified user data.' }
    $steps.Add('Repair preserves user data')
    Run-Native (Join-Path $destination 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG="C:\VoiceOutput\uninstall.log"')
    # Inno may hand off to a temporary uninstaller; wait for its tracked program file to vanish.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $exe) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    foreach ($file in $payload.files) {
        if (Test-Path -LiteralPath (Join-Path $destination $file.path)) { throw 'Tracked payload remained after uninstall.' }
    }
    if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Uninstall modified user data.' }
    if (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Egoist Voice Compact.lnk')) { throw 'Shortcut remained after uninstall.' }
    if (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5F84E54F-BE2E-46BA-970C-D1A774D3D239}_is1') { throw 'Uninstall registration remained.' }
    $steps.Add('Uninstall removes payload and registration, preserves user data')
    [ordered]@{passed=$true;generatedAt=[DateTime]::UtcNow.ToString('o');environment='Windows Sandbox';osVersion=[Environment]::OSVersion.Version.ToString();installerSha256=$receipt.sha256;steps=$steps} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    [ordered]@{passed=$false;generatedAt=[DateTime]::UtcNow.ToString('o');environment='Windows Sandbox';steps=$steps;error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
    exit 1
}
