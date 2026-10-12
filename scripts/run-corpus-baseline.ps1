[CmdletBinding()]
param(
  [string]$CorpusPath = (Join-Path $PSScriptRoot "..\tests\corpus"),
  [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\bench\baseline.json"),
  [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')]
  [string]$Label = "voice-2.1.1-dirty-baseline",
  [ValidateSet("baseline", "hotwords")]
  [string]$DecoderMode = "baseline",
  [ValidateSet("full", "quality-challenge")]
  [string]$Profile = "full",
  [string]$ProfilePath,
  [switch]$Record,
  [switch]$NoBuild,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

if ($DecoderMode -eq "hotwords") {
  if (-not $PSBoundParameters.ContainsKey("OutputPath")) {
    $OutputPath = Join-Path $PSScriptRoot "..\artifacts\bench\hotwords.json"
  }
  if (-not $PSBoundParameters.ContainsKey("Label")) {
    $Label = "voice-2.2-hotwords-candidate"
  }
}

$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$CorpusRoot = [IO.Path]::GetFullPath($CorpusPath)
$ReportPath = [IO.Path]::GetFullPath($OutputPath)
$CandidatePath = $ReportPath + ".candidate"
$ProgressPath = $CandidatePath + ".progress.json"
$Executable = Join-Path $ProjectRoot "bin\Release\net8.0-windows\Egoist.Voice.exe"
$BenchmarkProfilePath = if (-not [string]::IsNullOrWhiteSpace($ProfilePath)) {
  [IO.Path]::GetFullPath($ProfilePath)
} elseif ($Profile -eq "quality-challenge") {
  Join-Path $CorpusRoot "quality-challenge-v1.json"
} else {
  $null
}

if ($Profile -eq "quality-challenge" -and [string]::IsNullOrWhiteSpace($ProfilePath)) {
  if (-not $PSBoundParameters.ContainsKey("OutputPath")) {
    $ReportPath = Join-Path $ProjectRoot "artifacts\bench\quality-challenge-$DecoderMode.json"
    $CandidatePath = $ReportPath + ".candidate"
  }
  if (-not $PSBoundParameters.ContainsKey("Label")) {
    $Label = "voice-quality-challenge-$DecoderMode"
  }
}

if (-not (Test-Path -LiteralPath (Join-Path $CorpusRoot "script.jsonl") -PathType Leaf)) {
  throw "Corpus script is missing. Use the recorder workflow from tests/corpus/README.md."
}
if ($BenchmarkProfilePath -and -not (Test-Path -LiteralPath $BenchmarkProfilePath -PathType Leaf)) {
  throw "Corpus benchmark profile is missing."
}
if ((Test-Path -LiteralPath $ReportPath) -and -not $Force) {
  throw "Baseline already exists. Pass -Force only when intentionally replacing the frozen baseline."
}

if (-not $NoBuild) {
  & dotnet build (Join-Path $ProjectRoot "Egoist.Voice.sln") -c Release --no-restore --nologo
  if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
  }
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
  throw "Release executable is missing. Run without -NoBuild first."
}

if ($Record) {
  Write-Host "Recorder is private and local: WAV/reference.jsonl remain ignored by Git and are never logged."
  $RecorderArguments = @("--corpus-record", "`"$CorpusRoot`"")
  if ($BenchmarkProfilePath) {
    $RecorderArguments += "`"$BenchmarkProfilePath`""
  }
  $RecorderProcess = Start-Process -FilePath $Executable -ArgumentList $RecorderArguments -Wait -PassThru
  if ($RecorderProcess.ExitCode -ne 0) {
    throw "Corpus recorder failed with exit code $($RecorderProcess.ExitCode)."
  }
}

if (-not (Test-Path -LiteralPath (Join-Path $CorpusRoot "reference.jsonl") -PathType Leaf)) {
  throw "Private corpus is incomplete: reference.jsonl is missing. Run this command with -Record."
}

$ReportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
if (Test-Path -LiteralPath $CandidatePath) {
  Remove-Item -LiteralPath $CandidatePath -Force
}
if (Test-Path -LiteralPath $ProgressPath) {
  Remove-Item -LiteralPath $ProgressPath -Force
}

$BenchmarkArguments = @(
  "--corpus-benchmark",
  "`"$CorpusRoot`"",
  "`"$CandidatePath`"",
  $Label,
  $DecoderMode
)
if ($BenchmarkProfilePath) {
  $BenchmarkArguments += "`"$BenchmarkProfilePath`""
}
$BenchmarkProcess = Start-Process `
  -FilePath $Executable `
  -ArgumentList $BenchmarkArguments `
  -WindowStyle Hidden `
  -Wait `
  -PassThru
$BenchmarkExitCode = $BenchmarkProcess.ExitCode
if ($BenchmarkExitCode -ne 0) {
  $ProgressHint = "progress=unavailable"
  if (Test-Path -LiteralPath $ProgressPath -PathType Leaf) {
    $Progress = Get-Content -Raw -LiteralPath $ProgressPath | ConvertFrom-Json
    $ProgressHint = "progress=$($Progress.completedClips)/$($Progress.totalClips) phase=$($Progress.phase) id=$($Progress.currentId)"
  }
  throw "Offline corpus benchmark failed with exit code $BenchmarkExitCode; $ProgressHint. A managed failure may also leave an aggregate-only candidate report."
}
if (-not (Test-Path -LiteralPath $CandidatePath -PathType Leaf)) {
  throw "Corpus benchmark exited successfully without a report."
}

Move-Item -LiteralPath $CandidatePath -Destination $ReportPath -Force
if (Test-Path -LiteralPath $ProgressPath) {
  Remove-Item -LiteralPath $ProgressPath -Force
}
$Report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
$ProfileLabel = if ($Report.profile) { $Report.profile.id } else { "full" }
Write-Host "Corpus report frozen: profile=$ProfileLabel mode=$DecoderMode label=$($Report.label) clips=$($Report.corpus.clips) WER=$([math]::Round($Report.wer * 100, 2))% p95=$([math]::Round($Report.p95Ms))ms corpus=$($Report.corpus.sha256.Substring(0, 12))"
Write-Host "Privacy=$($Report.privacy); report contains metrics and stable IDs, never audio/reference/hypothesis text."
