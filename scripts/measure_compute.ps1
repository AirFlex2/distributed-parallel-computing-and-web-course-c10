# ==============================================================================
# scripts/measure_compute.ps1
# Відтворюваний скрипт експериментальних вимірювань РПО ЛР2 (О2.1, О2.2, О2.3, О2.4)
# Тема C10: Інтенсивність потоку заявок λ(t) = B + A·exp(-t/τ), t ∈ [0, T]
# ==============================================================================

param(
    [string]$BaseUrl = "http://localhost:8000",
    [string]$Token = "secret-c10-token-2026",
    [long]$BenchN = 250000000
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$AuthHeaders = @{
    "Authorization" = "Bearer $Token"
    "Content-Type"  = "application/json"
}

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

    return Invoke-RestMethod -Uri "$BaseUrl/v1/compute" -Method Post -Headers $AuthHeaders -Body $body -TimeoutSec 120
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
$wallMeans = @{}
foreach ($w in @(1, 2, 4)) {
    $runs = @()
    for ($run = 1; $run -le 3; $run++) {
        $r = Invoke-Compute -a 0.0 -b 60.0 -n $BenchN -workers $w -mode "local"
        $runs += [double]$r.wall_s
    }
    $sorted = $runs | Sort-Object
    $median = $sorted[1]
    $mean = ($runs[0] + $runs[1] + $runs[2]) / 3.0
    $wallMeans[$w] = $mean
    if ($w -eq 1) {
        $t1Mean = $mean
    }
    $speedup = $t1Mean / $mean
    $efficiency = $speedup / $w
    Write-Host ("workers={0} | run1={1:F4}s, run2={2:F4}s, run3={3:F4}s | mean={4:F4}s | median={5:F4}s | S(p)={6:F3} | E(p)={7:F3}" -f `
        $w, $runs[0], $runs[1], $runs[2], $mean, $median, $speedup, $efficiency)
}

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 3. ПРОФІЛЮВАННЯ ТА ПЕРЕВІРКА ГІПОТЕЗИ ОПТИМІЗАЦІЇ В ОДНАКОВИХ УМОВАХ (РПО О2.3)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

Write-Host "--- 3.1. Вимірювання інфраструктурної фази (spawn + CLR init + IPC, n = 100) ---" -ForegroundColor Yellow
$overheadMeans = @{}
foreach ($w in @(1, 2, 4)) {
    $smallRuns = @()
    for ($sr = 1; $sr -le 3; $sr++) {
        $rSmall = Invoke-Compute -a 0.0 -b 60.0 -n 100 -workers $w -mode "local"
        $smallRuns += [double]$rSmall.wall_s
    }
    $ohMean = ($smallRuns[0] + $smallRuns[1] + $smallRuns[2]) / 3.0
    $overheadMeans[$w] = $ohMean
    $totalW = $wallMeans[$w]
    $cpuLoopEst = [Math]::Max(0.0, $totalW - $ohMean)
    $seqSharePct = ($ohMean / $totalW) * 100.0
    Write-Host ("workers={0} | T_total={1:F4}s | T_spawn_ipc(n=100)={2:F4}s ({3:F2} ms) | T_compute_loop≈{4:F4}s | spawn_share={5:F2}%" -f `
        $w, $totalW, $ohMean, ($ohMean * 1000.0), $cpuLoopEst, $seqSharePct)
}

Write-Host "`n--- 3.2. Вбудований профайлер у Release-контейнері app-a (--compute-worker --profile-o23 --n $BenchN) ---" -ForegroundColor Yellow
Write-Host "Команда: docker compose exec -T app-a dotnet /app/EnrollmentService.dll --compute-worker --profile-o23 --n $BenchN"
$profileJsonRaw = docker compose exec -T app-a dotnet /app/EnrollmentService.dll --compute-worker --profile-o23 --n $BenchN
$profileData = $profileJsonRaw | ConvertFrom-Json

$naiveWall = [double]$profileData.before_optimization_naive.wall_s
$naiveCpu  = [double]$profileData.before_optimization_naive.cpu_s
$naiveExp  = $profileData.before_optimization_naive.math_exp_calls
$naiveVal  = [double]$profileData.before_optimization_naive.value

$optWall   = [double]$profileData.after_optimization_composite.wall_s
$optCpu    = [double]$profileData.after_optimization_composite.cpu_s
$optExp    = $profileData.after_optimization_composite.math_exp_calls
$optVal    = [double]$profileData.after_optimization_composite.value
$kSpeedup  = [double]$profileData.kernel_speedup_factor

$estFullBefore1W = $naiveWall + $overheadMeans[1]
$estFullAfter1W  = $wallMeans[1]
$fullEndpointSpeedup1W = $estFullBefore1W / $estFullAfter1W

Write-Host ("  [До оптимізації — наївна формула трапецій (2*n викликів Math.Exp + ділення -t/tau)]:")
Write-Host ("    Math.Exp calls = {0} | pure_loop_wall = {1:F4}s (CPU = {2:F4}s) | full_1_worker_wall ≈ {3:F4}s | value = {4:F10}" -f `
    $naiveExp, $naiveWall, $naiveCpu, $estFullBefore1W, $naiveVal)
Write-Host ("  [Після оптимізації — складена формула зі спільними вузлами ((n+1) викликів Math.Exp + множення invNegTau)]:")
Write-Host ("    Math.Exp calls = {0} | pure_loop_wall = {1:F4}s (CPU = {2:F4}s) | full_1_worker_wall (POST /v1/compute) = {3:F4}s | value = {4:F10}" -f `
    $optExp, $optWall, $optCpu, $estFullAfter1W, $optVal)
Write-Host ("  [Підсумок перевірки гіпотези в однакових умовах Release-контейнера]:")
Write-Host ("    Прискорення обчислювального ядра: {0:F3}x ({1:F4}s -> {2:F4}s)" -f $kSpeedup, $naiveWall, $optWall)
Write-Host ("    Прискорення повного виклику (1 процес із spawn+IPC): {0:F3}x ({1:F4}s -> {2:F4}s)" -f $fullEndpointSpeedup1W, $estFullBefore1W, $estFullAfter1W)

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 4. СПІЛЬНА СУМА ТА ЛОКАЛЬНІ РЕЗУЛЬТАТИ: none vs lock vs local (РПО О2.4)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

foreach ($mode in @("none", "lock", "local")) {
    foreach ($w in @(1, 2, 4)) {
        $rm = Invoke-Compute -a 0.0 -b 60.0 -n 10000000 -workers $w -mode $mode
        Write-Host ("mode={0,-5} | workers={1} | value={2,14:F6} | ref={3,14:F6} | abs_err={4:E4} | wall_s={5:F4}s" -f `
            $mode, $w, $rm.value, $rm.reference, $rm.abs_error, $rm.wall_s)
    }
}
