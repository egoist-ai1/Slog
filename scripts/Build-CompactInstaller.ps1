[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$StagingDirectory,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $projectRoot 'Egoist.Voice.csproj') -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$fileVersion = [string]$projectXml.Project.PropertyGroup.FileVersion
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stage = [IO.Path]::GetFullPath($StagingDirectory)
if (!$stage.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging must be inside project artifacts.' }
$output = Split-Path -Parent $stage
$manifestPath = Join-Path $output 'portable-stage.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$actual = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
if ($actual.Count -ne $manifest.fileCount) { throw 'Unexpected staging files.' }
foreach ($node in @(Get-Item -LiteralPath $stage) + @(Get-ChildItem -LiteralPath $stage -Recurse -Force)) {
    if ($node.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse payload refused.' }
}
$lines = foreach ($item in $manifest.files) {
    if ($item.path -match '(^|/)(\.\.?|Data)(/|$)' -or $item.path -match '[":;\r\n{}]' -or [IO.Path]::IsPathRooted($item.path)) { throw 'Unsafe payload entry.' }
    $source = [IO.Path]::GetFullPath((Join-Path $stage $item.path))
    if (!$source.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload escaped staging.' }
    if ((Get-Item -LiteralPath $source).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $source).Hash -ne $item.sha256) { throw ('Payload hash mismatch: ' + $item.path) }
    $relativeDirectory = [IO.Path]::GetDirectoryName($item.path.Replace('/','\'))
    $destination = '{app}' + $(if ($relativeDirectory) { '\' + $relativeDirectory } else { '' })
    'Source: "' + $source + '"; DestDir: "' + $destination + '"; Flags: ignoreversion'
}
$compiler = Join-Path $env:USERPROFILE '.nuget\packages\dotnet-innosetup\6.2.1\tools\is\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Restore the pinned dotnet tool manifest before building.' }
$installer = Join-Path $output ('EgoistVoice-Setup-Compact-RU-' + $version + '-win-x64.exe')
if (!$Build) {
    [pscustomobject]@{passed=$true;planOnly=$true;files=$actual.Count;unpackedBytes=$manifest.unpackedBytes;installer=$installer;compiler=$compiler} | ConvertTo-Json
    exit 0
}
if (Test-Path -LiteralPath $installer) { throw 'Existing installer preserved; choose a new artifact directory.' }
$include = Join-Path $output 'compact-payload.iss'
[IO.File]::WriteAllLines($include, [string[]]$lines, [Text.UTF8Encoding]::new($true))
$issFile = Join-Path $projectRoot 'installer\EgoistVoiceCompact.iss'
$issBytes = [IO.File]::ReadAllBytes($issFile)
if ($issBytes.Length -lt 3 -or $issBytes[0] -ne 0xEF -or $issBytes[1] -ne 0xBB -or $issBytes[2] -ne 0xBF) {
    $issContent = [IO.File]::ReadAllText($issFile, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($issFile, $issContent, [Text.UTF8Encoding]::new($true))
}
& $compiler ('/DPayloadInclude=' + $include) ('/DOutputDir=' + $output) ('/DAppVersion=' + $version) ('/DAppFileVersion=' + $fileVersion) $issFile > (Join-Path $output 'compact-installer-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Inno compilation failed; see compact-installer-build.log.' }
$size = (Get-Item -LiteralPath $installer).Length
if ($size -gt 900000000) { throw 'Installer exceeds 900 MB.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
[ordered]@{passed=$true;generatedAt=[DateTime]::UtcNow.ToString('o');installer=$installer;bytes=$size;sha256=(Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant();
    signature=$signature.Status.ToString();manifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant();compilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;
    installerExecutedOnHost=$false;cleanWindowsVerified=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'compact-installer.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'compact-installer.json')
