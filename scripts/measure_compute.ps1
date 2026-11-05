# ==============================================================================
# scripts/measure_compute.ps1
# Відтворюваний скрипт експериментальних вимірювань РПО ЛР2 (О2.1, О2.2, О2.3, О2.4)
# Тема C10: Інтенсивність потоку заявок λ(t) = B + A·exp(-t/τ)
# ==============================================================================

param(
    [string]$BaseUrl = "http://localhost:8000",
    [long]$BenchN = 250000000
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-Compute {
    param(
        [double]$a,
        [double]$b,
        [long]$n,
        [int]$workers,
        [string]$mode = "local"
    )
    $body = @{
        a       = $a
        b       = $b
        n       = $n
        workers = $workers
        method  = "trapezoid"
        order   = "natural"
        mode    = $mode
    } | ConvertTo-Json -Compress

    return Invoke-RestMethod -Uri "$BaseUrl/v1/compute" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 120
}

Write-Host "==============================================================================" -ForegroundColor Cyan
Write-Host " 1. ДОСЛІДЖЕННЯ ТОЧНОСТІ МЕТОДУ ТРАПЕЦІЙ (РПО О2.2): n та 2n на [0, 60]" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

$nSeries = @(100, 200, 400, 800, 1600, 3200, 10000, 20000, 100000, 200000)
$prevError = $null
foreach ($nVal in $nSeries) {
    $res = Invoke-Compute -a 0.0 -b 60.0 -n $nVal -workers 2 -mode "local"
    $ratioStr = "—"
    if ($null -ne $prevError -and $res.abs_error -gt 0) {
        $ratio = $prevError / $res.abs_error
        $ratioStr = "{0:F4}" -f $ratio
    }
    $prevError = $res.abs_error
    Write-Host ("n={0,7} | value={1:F10} | ref={2:F10} | abs_err={3:E4} | rel_err={4:E4} | err(n/2)/err(n)={5}" -f `
        $nVal, $res.value, $res.reference, $res.abs_error, $res.rel_error, $ratioStr)
}

Write-Host "`n--- Порівняння точності між workers = 1, 2, 4 при однаковому n = 200000 ---" -ForegroundColor Yellow
foreach ($w in @(1, 2, 4)) {
    $r = Invoke-Compute -a 0.0 -b 60.0 -n 200000 -workers $w -mode "local"
    Write-Host ("workers={0} | value={1:R} | abs_error={2:E6} | rel_error={3:E6}" -f $w, $r.value, $r.abs_error, $r.rel_error)
}

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 2. ПРИСКОРЕННЯ S(p) ТА ЕФЕКТИВНІСТЬ E(p) НА 1, 2, 4 ПРОЦЕСАХ (РПО О2.3)" -ForegroundColor Cyan
Write-Host "    bench_n = $BenchN (мінімум 3 прогони на кожну конфігурацію)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

# Прогрів (warm-up)
$null = Invoke-Compute -a 0.0 -b 60.0 -n 100000 -workers 4 -mode "local"

$t1Mean = 0.0
foreach ($w in @(1, 2, 4)) {
    $runs = @()
    for ($run = 1; $run -le 3; $run++) {
        $r = Invoke-Compute -a 0.0 -b 60.0 -n $BenchN -workers $w -mode "local"
        $runs += [double]$r.wall_s
    }
    $sorted = $runs | Sort-Object
    $median = $sorted[1]
    $mean = ($runs[0] + $runs[1] + $runs[2]) / 3.0
    if ($w -eq 1) {
        $t1Mean = $mean
    }
    $speedup = $t1Mean / $mean
    $efficiency = $speedup / $w
    Write-Host ("workers={0} | run1={1:F4}s, run2={2:F4}s, run3={3:F4}s | mean={4:F4}s | median={5:F4}s | S(p)={6:F3} | E(p)={7:F3}" -f `
        $w, $runs[0], $runs[1], $runs[2], $mean, $median, $speedup, $efficiency)
}

Write-Host "`n--- Оцінка накладних витрат запуску дочірніх процесів ОС та IPC (n = 100) ---" -ForegroundColor Yellow
foreach ($w in @(1, 2, 4)) {
    $rSmall = Invoke-Compute -a 0.0 -b 60.0 -n 100 -workers $w -mode "local"
    Write-Host ("n=100, workers={0} -> wall_s={1:F4}s ({2:F2} ms)" -f $w, $rSmall.wall_s, $rSmall.duration_ms)
}

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 3. СПІЛЬНА СУМА ТА ЛОКАЛЬНІ РЕЗУЛЬТАТИ: none vs lock vs local (РПО О2.4)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

foreach ($mode in @("none", "lock", "local")) {
    foreach ($w in @(1, 2, 4)) {
        $rm = Invoke-Compute -a 0.0 -b 60.0 -n 10000000 -workers $w -mode $mode
        Write-Host ("mode={0,-5} | workers={1} | value={2,14:F6} | ref={3,14:F6} | abs_err={4:E4} | wall_s={5:F4}s" -f `
            $mode, $w, $rm.value, $rm.reference, $rm.abs_error, $rm.wall_s)
    }
}
