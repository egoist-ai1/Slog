[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$schemaPath = Join-Path $PSScriptRoot 'benchmark-report-v4.schema.json'

$cleanJson = @'
{
  "generatedUtc": "2026-08-08T00:00:00Z",
  "label": "privacy-schema-test",
  "wer": 0,
  "cer": 0,
  "p50Ms": 1,
  "p95Ms": 2,
  "schema": "egoist.voice.corpus-benchmark/v4",
  "privacy": "aggregate-only-no-transcript",
  "failedClips": 0,
  "entityAccuracy": 1,
  "entitiesExpected": 0,
  "splitErrors": 0,
  "commandPrecision": 1,
  "commandRecall": 1,
  "punctuationF1": 1,
  "boundaryAccuracy": 1,
  "sets": [{
    "set": "ru-clean", "clips": 1, "wer": 0, "cer": 0, "failedClips": 0,
    "entityAccuracy": 1, "entitiesExpected": 0, "splitErrors": 0,
    "commandPrecision": 1, "commandRecall": 1, "punctuationF1": 1,
    "boundaryAccuracy": 1
  }],
  "entries": [{
    "id": "ru-clean/001", "set": "ru-clean", "perceivedMs": 1, "totalMs": 2,
    "wordErrors": 0, "referenceWords": 1, "characterErrors": 0,
    "referenceCharacters": 8, "entitiesExpected": 0, "entitiesCorrect": 0,
    "splitErrors": 0, "boundaryExpected": false, "boundaryCorrect": false,
    "buckets": ["pure-ru"], "captureCode": "CaptureUsable", "gateCode": "GateAccepted",
    "attributionCode": "NoFailure", "primaryWordErrors": 0,
    "selectedWordErrors": 0,
    "stageMs": {
      "captureAnalysis": 1, "primaryDecode": 1,
      "normalization": 1, "pipeline": 2
    }
  }],
  "corpus": {
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "clips": 1, "audioBytes": 1,
    "scriptSha256": "0000000000000000000000000000000000000000000000000000000000000000"
  },
  "environment": {
    "appVersion": "test", "runtime": ".NET 8", "os": "Windows",
    "processArchitecture": "X64", "logicalProcessors": 1,
    "models": [{
      "id": "gigaam", "bytes": 1,
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
    }]
  },
  "parameters": {
    "pipeline": "RussianSpeechQualityService", "inputSampleRateHz": 16000,
    "capturePreRollMs": 320, "captureReleaseTailMs": 350,
    "gigaAmThreads": 1, "gigaAmBatchThreshold": 1, "gigaAmMaxBatchSize": 1,
    "gigaAmContextualBias": false,
    "entityCatalogVersion": "test", "entityProfilePolicy": "test",
    "applyBuiltInDictionary": true, "applyVoiceCommands": true,
    "applyNumberNormalization": true, "modelDownloadAllowed": false
  },
  "resources": {
    "workingSetStartBytes": 0, "workingSetEndBytes": 0, "peakWorkingSetBytes": 0,
    "privateStartBytes": 0, "privateEndBytes": 0, "handlesStart": 0,
    "handlesEnd": 0, "managedStartBytes": 0, "managedEndBytes": 0
  },
  "profile": {
    "id": "quality-challenge-v1",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "clips": 1, "buckets": { "pure-ru": 1 }
  },
  "diagnostics": {
    "captureCodes": [{ "code": "CaptureUsable", "clips": 1 }],
    "gateCodes": [{ "code": "GateAccepted", "clips": 1 }],
    "attributions": [{ "code": "NoFailure", "clips": 1 }],
    "stageTimings": [{ "stage": "pipeline", "samples": 1, "p50Ms": 1, "p95Ms": 1 }],
    "buckets": [{
      "bucket": "pure-ru", "clips": 1, "failedClips": 0, "wer": 0,
      "gateAcceptedClips": 1
    }],
    "uiThreadStalls": { "samples": 1, "p50Ms": 25, "p95Ms": 25, "maxMs": 25, "over100Ms": 0 }
  }
}
'@

function Test-SchemaJson {
    param([Parameter(Mandatory)][string]$Json)

    return Test-Json -Json $Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue
}

function Get-ObjectAtPath {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path
    )

    $node = $Root
    if ($Path.Length -eq 0) {
        return $node
    }

    foreach ($segment in $Path.Split('.')) {
        $index = 0
        if ([int]::TryParse($segment, [ref]$index)) {
            $node = $node[$index]
        }
        else {
            $node = $node.$segment
        }
    }
    return $node
}

if (-not (Test-SchemaJson -Json $cleanJson)) {
    throw 'Clean benchmark-report/v4 fixture was rejected by its schema.'
}

$objectPaths = @(
    '',
    'sets.0',
    'entries.0',
    'entries.0.stageMs',
    'corpus',
    'environment',
    'environment.models.0',
    'parameters',
    'resources',
    'profile',
    'diagnostics',
    'diagnostics.captureCodes.0',
    'diagnostics.stageTimings.0',
    'diagnostics.buckets.0',
    'diagnostics.uiThreadStalls'
)
$forbiddenProperties = @('transcript', 'audioPath', 'audioContent', 'targetApplication')

foreach ($path in $objectPaths) {
    foreach ($property in $forbiddenProperties) {
        $fixture = $cleanJson | ConvertFrom-Json -Depth 100
        $target = Get-ObjectAtPath -Root $fixture -Path $path
        $target | Add-Member -NotePropertyName $property -NotePropertyValue 'PRIVATE-CANARY'
        $mutated = $fixture | ConvertTo-Json -Depth 100 -Compress
        if (Test-SchemaJson -Json $mutated) {
            throw "Schema accepted forbidden property '$property' at '$path'."
        }
    }
}

Write-Output "PASS: benchmark-report/v4 rejects $($forbiddenProperties.Count * $objectPaths.Count) sensitive-field mutations."
