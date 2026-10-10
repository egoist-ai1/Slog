[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$InstalledModelsRoot = (Join-Path $PSScriptRoot '..\artifacts\Russian-2.4.2-resource-verified\portable\Models'),
    [switch]$UseExistingPublish,
    [string]$WorkDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $artifactRoot ('portable-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')) }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (!$destination.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Portable staging must be inside this project artifacts directory.'
}
if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Reparse staging directory refused.'
}
if (!$UseExistingPublish) {
    if (Test-Path -LiteralPath $destination) { throw 'Choose a new staging directory; existing files are preserved.' }
    & dotnet publish (Join-Path $projectRoot 'Egoist.Voice.csproj') -c Release -r win-x64 --self-contained true '-p:VoiceFlavor=Compact' -o $destination --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
}
$executable = Join-Path $destination 'Egoist.Voice.exe'
if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath (Join-Path $destination 'coreclr.dll'))) {
    throw 'Expected self-contained publish is missing.'
}
if (Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object { $_.Name -match '^(cublas|cudart|ggml-cuda|whisper\.dll)' }) {
    throw 'Non-compact runtime detected.'
}
$manifestPath = Join-Path $destination 'compact-models.json'
$priorDataRoot = $env:EGOIST_VOICE_DATA_ROOT
$priorLogRoot = $env:EGOISTVOICE_LOG_DIRECTORY
try {
    $diagnosticRoot = if ($WorkDirectory) { [IO.Path]::GetFullPath($WorkDirectory) } else { Join-Path (Split-Path -Parent $destination) 'build-diagnostics' }
    $env:EGOIST_VOICE_DATA_ROOT = Join-Path $diagnosticRoot 'data'
    $env:EGOISTVOICE_LOG_DIRECTORY = Join-Path $diagnosticRoot 'logs'
    $process = Start-Process -FilePath $executable -ArgumentList @('--export-russian-quality-models', ('"' + $manifestPath + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Model manifest export timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Model manifest export failed.' }
} finally {
    $env:EGOIST_VOICE_DATA_ROOT = $priorDataRoot
    $env:EGOISTVOICE_LOG_DIRECTORY = $priorLogRoot
}
$models = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (@($models).Count -ne 8 -or @($models.Id | Sort-Object -Unique).Count -ne 8 -or ($models | Measure-Object SizeBytes -Sum).Sum -ne 650090519 -or
    @($models | Where-Object Id -Match '^gigaam-v3-e2e-rnnt-').Count -ne 4 -or
    @($models | Where-Object Id -Match '^gigaam-v3-rnnt-').Count -ne 4) {
    throw 'Russian quality requires eight primary and formatting RNNT assets totaling 650090519 bytes.'
}
foreach ($model in $models) {
    if ($model.Id -notmatch '^gigaam-[a-z0-9-]+$' -or $model.FileName -notmatch '^[a-zA-Z0-9_.-]+$') { throw 'Invalid catalog path.' }
    $relative = Join-Path 'Speech' (Join-Path $model.Id $model.FileName)
    $source = Join-Path $InstalledModelsRoot $relative
    if (!(Test-Path -LiteralPath $source)) { throw "Build model missing: $($model.Id). No downloads will be attempted." }
    if ((Get-Item -LiteralPath $source).Length -ne $model.SizeBytes -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $model.Sha256) { throw "Build model hash mismatch: $($model.Id)" }
    $target = Join-Path (Join-Path $destination 'Models') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $model.Sha256) { throw 'Copied model hash mismatch.' }
    @{ id=$model.Id; sizeBytes=$model.SizeBytes; sha256=$model.Sha256 } | ConvertTo-Json | Set-Content -LiteralPath ($target + '.verified.json') -Encoding utf8
}
$licenseDestination = Join-Path $destination 'Models\Licenses'
$licenseSource = Join-Path $InstalledModelsRoot 'Licenses'
if (Test-Path -LiteralPath $licenseSource -PathType Container) {
    if (Get-ChildItem -LiteralPath $licenseSource -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Reparse license input refused.' }
    New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $licenseSource -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $licenseDestination }
} else {
    New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\models\GigaAM-LICENSE.txt') -Destination (Join-Path $licenseDestination 'GigaAM-MIT-LICENSE.txt')
}
[IO.File]::WriteAllText((Join-Path $destination 'egoist-voice.portable'), 'gigaam-v3-rnnt-russian-quality-cpu-v4')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $destination
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $destination
[IO.File]::WriteAllText((Join-Path $destination 'START-HERE.txt'), @'
Слог — офлайн-диктовка на русском. Windows 10 (1903 и новее) / Windows 11, x64

Запустите Egoist.Voice.exe из установленной или перенесённой целиком папки.
.NET и отдельная видеокарта не нужны. Модели уже включены; сеть не требуется.
Удерживайте настроенную кнопку мыши или выберите сочетание клавиш в меню трея.
Настройки и журнал пишутся в Data рядом с приложением. История 3 последних записей включается отдельно в настройках; ваш выбор сохраняется при обновлении.
Переносите всю папку. Перед переносом закройте приложение.

Состав: русский профиль GigaAM v3 (Слог 3.2), CPU, 8 модельных файлов. Модель всегда готова в памяти, загрузки перед диктовкой нет.
Plain RNNT INT8 считывает слова; E2E RNNT INT8 определяет пунктуацию и регистр по аудио.
Оформление по голосу включено по умолчанию. Явный выбор выключить его сохраняется при обновлении.
Профиль осторожно уточняет известные английские названия при подтверждении аудиораспознавателем.
Ошибки распознавания возможны. Дополнительная текстовая модель и перефразирование не нужны.
Whisper и переводчик не входят.
«Текст» позволяет распознать аудиофайл и скопировать результат.
«История» позволяет слушать, удалять и повторно распознавать три последние записи.

Закройте установленный Слог перед запуском другой копии: они используют один hotkey.
Это неподписанная локальная сборка. На другом устройстве проверьте выбранный микрофон и клавишу активации.
'@)
$files = @(Get-ChildItem -LiteralPath $destination -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject][ordered]@{ path=$_.FullName.Substring($destination.Length + 1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$total = ($files | Measure-Object bytes -Sum).Sum
if ($total -gt 1000000000) { throw "Russian quality folder exceeds 1 GB: $total bytes" }
$gitCmd = Get-Command git.exe -ErrorAction SilentlyContinue
$gitExe = if ($gitCmd) { $gitCmd.Source } else { $null }
if (!$gitExe -and (Test-Path 'C:\Users\Egoist\AppData\Local\GitHubDesktop\app-3.6.5\resources\app\git\cmd\git.exe')) {
    $gitExe = 'C:\Users\Egoist\AppData\Local\GitHubDesktop\app-3.6.5\resources\app\git\cmd\git.exe'
}
$sourceRev = "unavailable"
$sourceDirty = $false
if ($gitExe) {
    try {
        $rev = (& $gitExe -C $projectRoot rev-parse HEAD 2>$null)
        if ($rev) { $sourceRev = $rev.Trim() }
        $dirty = @(& $gitExe -C $projectRoot status --porcelain 2>$null)
        $sourceDirty = [bool]($dirty.Count)
    } catch { Write-Verbose ('Source revision unavailable: ' + $_.Exception.Message) }
}
[xml]$versionDocument = Get-Content -LiteralPath (Join-Path $projectRoot 'Egoist.Voice.csproj') -Raw
$receipt = [ordered]@{ schemaVersion=1; version=[string]$versionDocument.Project.PropertyGroup.Version; fileVersion=[string]$versionDocument.Project.PropertyGroup.FileVersion; generatedAt=[DateTime]::UtcNow.ToString('o'); flavor='Russian Quality CPU Portable'; modelAssetCount=8; modelAssetBytes=650090519; unpackedBytes=$total; fileCount=$files.Count; sourceRevision=$sourceRev; sourceDirty=$sourceDirty; files=$files }
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $destination) 'portable-stage.manifest.json') -Encoding utf8
[pscustomobject]@{ Staging=$destination; Files=$files.Count; Bytes=$total; MB=[math]::Round($total / 1000000, 2) } | ConvertTo-Json
