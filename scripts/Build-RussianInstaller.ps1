[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$SpeechModelsRoot = (Join-Path $PSScriptRoot '..\artifacts\models'),
    [string]$TextModelPath,
    [string]$TextRuntimeZip,
    [string]$VcRuntimeDirectory,
    [switch]$IncludeTextEditor,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $project 'artifacts'
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$work = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\')
if (!$output.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be inside project artifacts.' }
if ($output.Contains('"') -or $work.Contains('"')) { throw 'Quoted build paths are unsupported.' }
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory; existing artifacts are preserved.' }
$inputPaths = @($SpeechModelsRoot)
if ($IncludeTextEditor) {
    if (!$TextModelPath -or !$TextRuntimeZip -or !$VcRuntimeDirectory) { throw 'Text editor requires model, runtime and VC runtime paths.' }
    $inputPaths += @($TextModelPath, $TextRuntimeZip, $VcRuntimeDirectory)
}
foreach ($inputPath in $inputPaths) {
    $item = Get-Item -LiteralPath $inputPath
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse input refused.' }
}
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $project 'Egoist.Voice.csproj') -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$fileVersion = [string]$projectXml.Project.PropertyGroup.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid project version.' }
$compiler = Join-Path $env:USERPROFILE '.nuget\packages\dotnet-innosetup\6.2.1\tools\is\ISCC.exe'
$bootstrapCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($toolPath in @($compiler, $bootstrapCompiler)) {
    if (!(Test-Path -LiteralPath $toolPath -PathType Leaf)) { throw 'Pinned installer compiler is missing.' }
}
if (!$Build) {
    [ordered]@{ planOnly=$true; version=$version; output=$output; work=$work; includeTextEditor=[bool]$IncludeTextEditor; modelAssetCount=8; modelAssetBytes=650090519; formatSpeechPunctuation=$true; scope='Offline Russian quality GigaAM; optional Qwen; package only, no installation or downloads' } | ConvertTo-Json
    return
}
function Assert-FileHash([string]$Path, [string]$Sha256, [long]$Bytes = -1) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse file refused.' }
    if (($Bytes -ge 0 -and $item.Length -ne $Bytes) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw ('Input integrity failure: ' + $item.Name)
    }
}
if ($IncludeTextEditor) {
Assert-FileHash -Path $TextModelPath -Sha256 '7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5' -Bytes 2497280256
Assert-FileHash -Path $TextRuntimeZip -Sha256 'a63bd0ceab781483a7fde174f1676d86c9724d7376d721fab026fa2df1393997'
$vcHashes = [ordered]@{
    'msvcp140.dll' = '0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9'
    'vcruntime140.dll' = 'd5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066'
    'vcruntime140_1.dll' = '1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f'
}
foreach ($entry in $vcHashes.GetEnumerator()) { Assert-FileHash -Path (Join-Path $VcRuntimeDirectory $entry.Key) -Sha256 $entry.Value }
}
New-Item -ItemType Directory -Path $output, $work -Force | Out-Null
$stage = Join-Path $output 'portable'
& dotnet publish (Join-Path $project 'Egoist.Voice.csproj') -c Release -r win-x64 --self-contained true '-p:VoiceFlavor=Compact' '-p:RuntimeFrameworkVersion=8.0.30' --no-restore -o $stage --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed. Restore the win-x64 Compact target first.' }
& (Join-Path $PSScriptRoot 'Build-CompactPortable.ps1') -OutputDirectory $stage -InstalledModelsRoot $SpeechModelsRoot -UseExistingPublish -WorkDirectory (Join-Path $work 'catalog-check') | Out-Null
if ($IncludeTextEditor) {
$textModels = Join-Path $stage 'TextModels'
$textRuntime = Join-Path $stage 'TextRuntime'
New-Item -ItemType Directory -Path $textModels, $textRuntime -Force | Out-Null
Copy-Item -LiteralPath $TextModelPath -Destination (Join-Path $textModels 'Qwen3-4B-Q4_K_M.gguf')
Assert-FileHash -Path (Join-Path $textModels 'Qwen3-4B-Q4_K_M.gguf') -Sha256 '7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5' -Bytes 2497280256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($TextRuntimeZip)
try {
    $required = @('llama-server.exe', 'llama-server-impl.dll', 'llama-common.dll', 'llama.dll', 'mtmd.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-vulkan.dll', 'libomp140.x86_64.dll')
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notin $required -and $entry.FullName -notmatch '^ggml-cpu-[a-z0-9]+\.dll$') { continue }
        if ($entry.FullName -ne [IO.Path]::GetFileName($entry.FullName)) { throw 'Unexpected runtime archive path.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $textRuntime $entry.FullName), $false)
    }
    foreach ($name in $required + @('ggml-cpu-x64.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $textRuntime $name))) { throw ('Missing packaged runtime dependency: ' + $name) }
    }
} finally { $zip.Dispose() }
foreach ($entry in $vcHashes.GetEnumerator()) { Copy-Item -LiteralPath (Join-Path $VcRuntimeDirectory $entry.Key) -Destination $textRuntime }
}
Copy-Item -LiteralPath (Join-Path $project 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse
$utf8 = [Text.UTF8Encoding]::new($false)
function Get-RussianInstallerConfiguration {
    return [ordered]@{
        preserveSpokenWords=$true; formatSpeechPunctuation=$true; formatWithQwen=$false; startLocalQwen=$false
        textModelEndpoint='http://127.0.0.1:47823/v1'; textModelId='egoist-qwen3-4b'; formatBudgetSeconds=2
        applyDictionary=$true; saveRecentRecordings=$false
    }
}
$defaults = Get-RussianInstallerConfiguration | ConvertTo-Json -Compress
$defaultsPath = Join-Path $work 'dictation-defaults.json'
[IO.File]::WriteAllText($defaultsPath, $defaults, $utf8)
[IO.File]::WriteAllText((Join-Path $stage 'START-HERE.txt'), @'
Egoist Voice — русская диктовка, офлайн

Закройте прежний Voice через трей. Запустите Egoist.Voice.exe.
GigaAM v3: 8 модельных файлов (plain RNNT INT8 + E2E RNNT INT8) и .NET включены. Настройки находятся в Data рядом с приложением.
Обычная сборка не включает Qwen; её можно добавить отдельным параметром сборки.
По умолчанию сохраняются произнесённые слова; пунктуация и регистр определяются по аудио.
Известные английские названия уточняются только при подтверждении аудиораспознавателем.
Ошибки распознавания возможны. Перефразирование не применяется.
При обновлении сохраняется явный выбор выключить оформление по голосу.
Если раньше этого поля не было, оформление включится. История в новой установке выключена.
В переносимой папке настройте историю во вкладке «Общие» перед записью.
Распознаватель остаётся в памяти. Пятиминутного таймера выгрузки нет.
Длительность аудиофайла не ограничена таймером; обработка идёт фрагментами.
Доступная память и место на диске остаются физическими ограничениями.
Whisper и движок перевода не входят.
Это неподписанная локальная сборка. Установка на чистой Windows требует отдельной проверки.
'@, $utf8)
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
    if ($relative -match '(^|/)(Data|Logs|Backups|\.\.)(/|$)|\.(wav|aac|mp3|log)$' -or $relative -match '[":;\r\n{}]' -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Private or unsafe staging entry.' }
    [pscustomobject][ordered]@{ path=$relative; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$sourceRevision = (& git -C $project rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not bind source revision.' }
$sourceDirty = [bool](@(& git -C $project status --porcelain).Count)
if ($sourceDirty) { throw 'Freeze and commit the reviewed release source before packaging.' }
$manifest = [ordered]@{ schemaVersion=1; version=$version; sourceDirty=$sourceDirty; fileVersion=$fileVersion; sourceRevision=$sourceRevision; modelAssetCount=8; modelAssetBytes=650090519; formatSpeechPunctuation=$true; fileCount=$files.Count; unpackedBytes=($files | Measure-Object bytes -Sum).Sum; files=$files; defaults=[ordered]@{ path='Data/dictation.json'; sha256=(Get-FileHash -LiteralPath $defaultsPath).Hash.ToLowerInvariant(); preserveOnUpgrade=$true; preserveOnUninstall=$true } }
[IO.File]::WriteAllText((Join-Path $output 'russian-payload.manifest.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)
[IO.File]::WriteAllText((Join-Path $output 'portable-stage.manifest.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)
$include = Join-Path $work 'russian-payload.iss'
$lines = @($files | ForEach-Object {
    $source = Join-Path $stage $_.path
    $relativeDirectory = [IO.Path]::GetDirectoryName($_.path.Replace('/', '\'))
    $destination = '{app}' + $(if ($relativeDirectory) { '\' + $relativeDirectory } else { '' })
    'Source: "' + $source + '"; DestDir: "' + $destination + '"; Flags: ignoreversion'
}) + @('Source: "' + $defaultsPath + '"; DestDir: "{app}\Data"; DestName: "dictation.json"; Flags: onlyifdoesntexist uninsneveruninstall')
[IO.File]::WriteAllLines($include, [string[]]$lines, [Text.UTF8Encoding]::new($true))
$inner = Join-Path $work 'inner'
New-Item -ItemType Directory -Path $inner | Out-Null
$editionArguments = @('/DRussianEdition=1')
if ($IncludeTextEditor) { $editionArguments += '/DBundleTextEditor=1' }
& $compiler '/Qp' ('/DPayloadInclude=' + $include) ('/DOutputDir=' + $inner) ('/DAppVersion=' + $version) ('/DAppFileVersion=' + $fileVersion) @editionArguments (Join-Path $project 'installer\EgoistVoiceCompact.iss') *> (Join-Path $work 'inno-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Inno compilation failed; inspect the scoped inno-build.log.' }
$innerExe = Join-Path $inner ('EgoistVoice-Setup-Russian-' + $version + '-win-x64-inner.exe')
$innerFiles = @((Get-Item -LiteralPath $innerExe)) + @(Get-ChildItem -LiteralPath $inner -Filter '*.bin' -File | Sort-Object Name)
if ($innerFiles.Count -lt 2) { throw 'Expected a disk-spanning installer payload.' }
$assemblyInfo = Join-Path $work 'BootstrapAssemblyInfo.cs'
[IO.File]::WriteAllText($assemblyInfo, @"
using System.Reflection;
[assembly: AssemblyTitle("Egoist Voice Installer")]
[assembly: AssemblyDescription("Offline Russian RNNT dictation")]
[assembly: AssemblyCompany("EGOIST")]
[assembly: AssemblyProduct("Egoist Voice")]
[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@, $utf8)
$stub = Join-Path $work 'EgoistVoiceSetupStub.exe'
& $bootstrapCompiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:' + $stub) ('/win32icon:' + (Join-Path $project 'assets\EgoistVoice.ico')) /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $project 'installer\EgoistVoiceBootstrap.cs') $assemblyInfo
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap compilation failed.' }
$installer = Join-Path $output ('EgoistVoice-Setup-Russian-' + $version + '-win-x64.exe')
& (Join-Path $PSScriptRoot 'New-EgoistVoiceSingleFile.ps1') -StubPath $stub -PayloadFiles @($innerFiles.FullName) -LaunchFileName ([IO.Path]::GetFileName($innerExe)) -OutputPath $installer | Out-Null
$verified = & (Join-Path $PSScriptRoot 'Test-EgoistVoiceSingleFile.ps1') -Path $installer -PassThru
if ($verified.LaunchFile -ne [IO.Path]::GetFileName($innerExe) -or @($verified.Entries).Count -ne $innerFiles.Count) { throw 'Embedded package identity mismatch.' }
$receipt = [ordered]@{ passed=$true; generatedAt=[DateTime]::UtcNow.ToString('o'); version=$version; sourceRevision=$sourceRevision; installer=$installer; bytes=(Get-Item -LiteralPath $installer).Length; sha256=(Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant(); payloadManifestSha256=(Get-FileHash -LiteralPath (Join-Path $output 'russian-payload.manifest.json')).Hash.ToLowerInvariant(); signature=(Get-AuthenticodeSignature -LiteralPath $installer).Status.ToString(); embeddedFileCount=@($verified.Entries).Count; installerExecutedOnHost=$false; cleanWindowsVerified=$false }
[IO.File]::WriteAllText((Join-Path $output 'russian-installer.json'), ($receipt | ConvertTo-Json -Depth 5), $utf8)
[IO.File]::WriteAllText(($installer + '.sha256'), ($receipt.sha256 + '  ' + [IO.Path]::GetFileName($installer) + "`n"), $utf8)
$receipt | ConvertTo-Json -Depth 5
