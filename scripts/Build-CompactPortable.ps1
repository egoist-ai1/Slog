[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$InstalledModelsRoot = (Join-Path $PSScriptRoot '..\artifacts\models-whisper\Models'),
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
if (Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object { $_.Name -match '^(cublas|cudart|cudnn|ggml-cuda)' }) {
    throw 'Non-compact runtime detected.'
}
$manifestPath = Join-Path $destination 'compact-models.json'
$priorDataRoot = $env:EGOIST_VOICE_DATA_ROOT
$priorLogRoot = $env:EGOISTVOICE_LOG_DIRECTORY
try {
    $diagnosticRoot = if ($WorkDirectory) { [IO.Path]::GetFullPath($WorkDirectory) } else { Join-Path (Split-Path -Parent $destination) 'build-diagnostics' }
    $env:EGOIST_VOICE_DATA_ROOT = Join-Path $diagnosticRoot 'data'
    $env:EGOISTVOICE_LOG_DIRECTORY = Join-Path $diagnosticRoot 'logs'
    $process = Start-Process -FilePath $executable -ArgumentList @('--export-whisper-models', ('"' + $manifestPath + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Model manifest export timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Model manifest export failed.' }
} finally {
    $env:EGOIST_VOICE_DATA_ROOT = $priorDataRoot
    $env:EGOISTVOICE_LOG_DIRECTORY = $priorLogRoot
}
$models = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (@($models).Count -ne 1 -or $models[0].Id -ne 'whisper-large-v3-turbo-q5_0-v1' -or $models[0].SizeBytes -ne 574041195 -or
    $models[0].Sha256 -ne '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2') {
    throw 'Slog requires exactly the pinned Whisper large-v3-turbo q5_0 asset (574041195 bytes).'
}
foreach ($model in $models) {
    if ($model.Id -notmatch '^whisper-[a-z0-9_-]+$' -or $model.FileName -notmatch '^[a-zA-Z0-9_.-]+$') { throw 'Invalid catalog path.' }
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
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\models\Whisper-LICENSE.txt') -Destination (Join-Path $licenseDestination 'Whisper-MIT-LICENSE.txt')
}
[IO.File]::WriteAllText((Join-Path $destination 'egoist-voice.portable'), 'whisper-large-v3-turbo-q5-ru-vulkan-v1')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $destination
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $destination
[IO.File]::WriteAllText((Join-Path $destination 'START-HERE.txt'), @'
Слог — офлайн-диктовка на русском. Windows 10 (1903 и новее) / Windows 11, x64

Запустите Egoist.Voice.exe из установленной или перенесённой целиком папки.
.NET не нужен. Модель включена; сеть не требуется.
Распознавание: Whisper large-v3-turbo (q5_0), русский язык, ускорение на видеокарте через Vulkan.
Без видеокарты работает на процессоре, но заметно медленнее.
Модель загружается в память на время диктовки и выгружается через минуту простоя.
Удерживайте настроенную кнопку мыши или выберите сочетание клавиш в меню трея.
Эта кнопка мыши не передаётся другим программам.
Настройки и журнал пишутся в Data рядом с приложением.
Ошибки распознавания возможны. Перевод и дополнительная текстовая модель не входят.

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
