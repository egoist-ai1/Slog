# Последовательный прогон конфигураций --asr-eval (замеры строго по одному). Вывод: artifacts\bench\slog32\<имя>.json
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'bin\Release\net8.0-windows\Egoist.Voice.exe'
$out = Join-Path $root 'artifacts\bench\slog32'
New-Item -ItemType Directory -Force $out | Out-Null
$matrix = [ordered]@{
    'a-int8'    = @{}
    'b-plain'   = @{ EGOIST_EVAL_PLAIN_ONLY = '1' }
    'c-gain-20' = @{ EGOIST_EVAL_GAIN_DB = '-20' }
    'c-gain-30' = @{ EGOIST_EVAL_GAIN_DB = '-30' }
    'd-tail250' = @{ EGOIST_EVAL_TAIL_MS = '250' }
    'e-fp32'    = @{ EGOIST_EVAL_ENCODER = 'fp32' }
    'f-beam4'   = @{ EGOIST_EVAL_DECODING = 'beam4' }
}
foreach ($name in $matrix.Keys) {
    Get-ChildItem Env:EGOIST_EVAL_* -ErrorAction SilentlyContinue | Remove-Item
    foreach ($kv in $matrix[$name].GetEnumerator()) { Set-Item "Env:$($kv.Key)" $kv.Value }
    $p = Start-Process -FilePath $exe -ArgumentList '--asr-eval', ('"' + (Join-Path $root 'tests\corpus') + '"'), ('"' + (Join-Path $out "$name.json") + '"') -Wait -PassThru
    "$name exit=$($p.ExitCode)"
}
