# Прогон конфигураций с пиковой нормализацией. Использование: run-asr-eval-norm.ps1 [-Only n1-gain30,...]
param([string[]]$Only)
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split "," })
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'bin\Release\net8.0-windows\Egoist.Voice.exe'
$out = Join-Path $root 'artifacts\bench\slog32'
$norm = @{ EGOIST_EVAL_NORMALIZE_PEAK_DB = '-3'; EGOIST_EVAL_NORMALIZE_MAX_GAIN_DB = '30' }
function M($extra) { $h = @{} + $norm; foreach ($k in $extra.Keys) { $h[$k] = $extra[$k] }; $h }
$matrix = [ordered]@{
    'a-int8-r2'       = @{}
    'n1-gain30-norm'  = M @{ EGOIST_EVAL_GAIN_DB = '-30' }
    'n2-gain20-norm'  = M @{ EGOIST_EVAL_GAIN_DB = '-20' }
    'n3-gain40-norm'  = M @{ EGOIST_EVAL_GAIN_DB = '-40' }
    'n4-orig-norm'    = M @{}
    'n5-orig-norm-below12' = M @{ EGOIST_EVAL_NORMALIZE_ONLY_BELOW_DB = '-12' }
    'n6-gain30-norm-tail250' = M @{ EGOIST_EVAL_GAIN_DB = '-30'; EGOIST_EVAL_TAIL_MS = '250' }
    'n1-gain30-norm-r2' = M @{ EGOIST_EVAL_GAIN_DB = '-30' }
    'n2-gain20-norm-r2' = M @{ EGOIST_EVAL_GAIN_DB = '-20' }
    'n4-orig-norm-r2' = M @{}
    'n5-orig-norm-below12-r2' = M @{ EGOIST_EVAL_NORMALIZE_ONLY_BELOW_DB = '-12' }
    'n6-gain30-norm-tail250-r2' = M @{ EGOIST_EVAL_GAIN_DB = '-30'; EGOIST_EVAL_TAIL_MS = '250' }
}
foreach ($name in $matrix.Keys) {
    if ($Only -and $Only -notcontains $name) { continue }
    Get-ChildItem Env:EGOIST_EVAL_* -ErrorAction SilentlyContinue | Remove-Item
    foreach ($kv in $matrix[$name].GetEnumerator()) { Set-Item "Env:$($kv.Key)" $kv.Value }
    $p = Start-Process -FilePath $exe -ArgumentList '--asr-eval', ('"' + (Join-Path $root 'tests\corpus') + '"'), ('"' + (Join-Path $out "$name.json") + '"') -Wait -PassThru
    "$name exit=$($p.ExitCode)"
}
