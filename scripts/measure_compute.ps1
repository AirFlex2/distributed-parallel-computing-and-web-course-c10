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
        [string]$mode = "local",
        [string]$kernel = "composite"
    )
    $body = @{
        a       = $a
        b       = $b
        n       = $n
        workers = $workers
        method  = "trapezoid"
        order   = "natural"
        mode    = $mode
        kernel  = $kernel
    } | ConvertTo-Json -Compress

    return Invoke-RestMethod -Uri "$BaseUrl/v1/compute" -Method Post -Headers $AuthHeaders -Body $body -TimeoutSec 180
}

function Get-RunStats {
    param([double[]]$runs)
    $sorted = $runs | Sort-Object
    $min = [double]$sorted[0]
    $max = [double]$sorted[$sorted.Count - 1]
    $median = [double]$sorted[[int][Math]::Floor($sorted.Count / 2)]
    $sum = 0.0
    foreach ($v in $runs) { $sum += $v }
    $mean = $sum / $runs.Count
    $spreadAbs = $max - $min
    $spreadPct = if ($median -gt 0) { ($spreadAbs / $median) * 100.0 } else { 0.0 }
    return [PSCustomObject]@{
        Runs      = $runs
        Min       = $min
        Max       = $max
        Median    = $median
        Mean      = $mean
        SpreadAbs = $spreadAbs
        SpreadPct = $spreadPct
    }
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
Write-Host " 2. ПРИСКОРЕННЯ S(p), ЕФЕКТИВНІСТЬ E(p) ТА РОЗКИД НА 1, 2, 4 ПРОЦЕСАХ (РПО О2.3)" -ForegroundColor Cyan
Write-Host "    bench_n = $BenchN (3 незалежні прогони без профайлера на кожну конфігурацію)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

# Прогрів (warm-up)
$null = Invoke-Compute -a 0.0 -b 60.0 -n 100000 -workers 4 -mode "local" -kernel "composite"

$statsByWorkers = @{}
foreach ($w in @(1, 2, 4)) {
    $runs = @()
    for ($run = 1; $run -le 3; $run++) {
        $r = Invoke-Compute -a 0.0 -b 60.0 -n $BenchN -workers $w -mode "local" -kernel "composite"
        $runs += [double]$r.wall_s
    }
    $st = Get-RunStats -runs $runs
    $statsByWorkers[$w] = $st

    $t1Med  = $statsByWorkers[1].Median
    $t1Mean = $statsByWorkers[1].Mean
    $sMed   = $t1Med / $st.Median
    $eMed   = $sMed / $w
    $sMean  = $t1Mean / $st.Mean
    $eMean  = $sMean / $w

    Write-Host ("workers={0} | runs=[{1:F4}s, {2:F4}s, {3:F4}s] | median={4:F4}s | mean={5:F4}s | spread={6:F4}s ({7:F2}%, [{8:F4}..{9:F4}]s) | S_med={10:F3} (E_med={11:F3}) | S_mean={12:F3} (E_mean={13:F3})" -f `
        $w, $runs[0], $runs[1], $runs[2], $st.Median, $st.Mean, $st.SpreadAbs, $st.SpreadPct, $st.Min, $st.Max, $sMed, $eMed, $sMean, $eMean)
}

Write-Host "`n--- 2.1. Вимірювання інфраструктурної фази (spawn + CLR init + IPC, n = 100, 3 прогони) ---" -ForegroundColor Yellow
$overheadByWorkers = @{}
foreach ($w in @(1, 2, 4)) {
    $smallRuns = @()
    for ($sr = 1; $sr -le 3; $sr++) {
        $rSmall = Invoke-Compute -a 0.0 -b 60.0 -n 100 -workers $w -mode "local" -kernel "composite"
        $smallRuns += [double]$rSmall.wall_s
    }
    $ohSt = Get-RunStats -runs $smallRuns
    $overheadByWorkers[$w] = $ohSt
    $totalMed = $statsByWorkers[$w].Median
    $cpuLoopMed = [Math]::Max(0.0, $totalMed - $ohSt.Median)
    $seqSharePct = ($ohSt.Median / $totalMed) * 100.0
    Write-Host ("workers={0} | T_total_med={1:F4}s | T_spawn_ipc_med(n=100)={2:F4}s ({3:F2} ms, spread={4:F2} ms) | T_compute_loop_est≈{5:F4}s | spawn_share={6:F2}%" -f `
        $w, $totalMed, $ohSt.Median, ($ohSt.Median * 1000.0), ($ohSt.SpreadAbs * 1000.0), $cpuLoopMed, $seqSharePct)
}

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 3. ПРОФІЛЮВАННЯ СТЕКА МЕТОДІВ (dotnet-trace) ТА БАГАТОПРОГОННА ПЕРЕВІРКА" -ForegroundColor Cyan
Write-Host "    ГІПОТЕЗИ ОПТИМІЗАЦІЇ БЕЗ ПРОФАЙЛЕРА В ОДНАКОВИХ УМОВАХ (РПО О2.3)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

Write-Host "--- 3.1. Семплююче профілювання стека викликів CPU через dotnet-trace у контейнері app-a ---" -ForegroundColor Yellow
Write-Host "Інструмент: dotnet-trace collect (провайдер Microsoft-DotNETCore-SampleProfiler) + dotnet-trace report topN"

Write-Host "`n[Профіль 1: ДО оптимізації (--kernel naive, n = $BenchN)]" -ForegroundColor Magenta
Write-Host "Команда: docker compose exec -T app-a dotnet-trace collect -o /tmp/c10_naive.nettrace -- dotnet /app/EnrollmentService.dll --compute-worker --a 0 --b 60 --n $BenchN --start 0 --end $BenchN --workers 1 --mode local --kernel naive"
$null = docker compose exec -T app-a dotnet-trace collect -o /tmp/c10_naive.nettrace -- dotnet /app/EnrollmentService.dll --compute-worker --a 0 --b 60 --n $BenchN --start 0 --end $BenchN --workers 1 --mode local --kernel naive
$reportNaive = docker compose exec -T app-a dotnet-trace report /tmp/c10_naive.nettrace topN -n 5
$reportNaive | ForEach-Object { Write-Host "  $_" }

Write-Host "`n[Профіль 2: ПІСЛЯ оптимізації (--kernel composite, n = $BenchN)]" -ForegroundColor Magenta
Write-Host "Команда: docker compose exec -T app-a dotnet-trace collect -o /tmp/c10_composite.nettrace -- dotnet /app/EnrollmentService.dll --compute-worker --a 0 --b 60 --n $BenchN --start 0 --end $BenchN --workers 1 --mode local --kernel composite"
$null = docker compose exec -T app-a dotnet-trace collect -o /tmp/c10_composite.nettrace -- dotnet /app/EnrollmentService.dll --compute-worker --a 0 --b 60 --n $BenchN --start 0 --end $BenchN --workers 1 --mode local --kernel composite
$reportComposite = docker compose exec -T app-a dotnet-trace report /tmp/c10_composite.nettrace topN -n 5
$reportComposite | ForEach-Object { Write-Host "  $_" }

$null = docker compose exec -T app-a rm -f /tmp/c10_naive.nettrace /tmp/c10_composite.nettrace

Write-Host "`n--- 3.2. Відтворюване порівняння ДО та ПІСЛЯ оптимізації БЕЗ профайлера (3 прогони POST /v1/compute) ---" -ForegroundColor Yellow
Write-Host "Умови: Release-контейнер app-a, POST /v1/compute, n = $BenchN, [a, b] = [0, 60], по 3 свіжі незалежні прогони для naive та composite без інструментації профайлера (чередування для нівелювання температурного впливу та прогріву)."

foreach ($wOpt in @(1, 4)) {
    $naiveRuns = @()
    $compRuns  = @()
    $naiveVal  = 0.0
    $compVal   = 0.0

    # Чередуємо запуски naive та composite у кожному з 3 раундів вимірювання
    for ($rIdx = 1; $rIdx -le 3; $rIdx++) {
        $rn = Invoke-Compute -a 0.0 -b 60.0 -n $BenchN -workers $wOpt -mode "local" -kernel "naive"
        $naiveRuns += [double]$rn.wall_s
        $naiveVal = [double]$rn.value

        $rc = Invoke-Compute -a 0.0 -b 60.0 -n $BenchN -workers $wOpt -mode "local" -kernel "composite"
        $compRuns += [double]$rc.wall_s
        $compVal = [double]$rc.value
    }

    $naiveSt  = Get-RunStats -runs $naiveRuns
    $compSt   = Get-RunStats -runs $compRuns
    $gainMed  = $naiveSt.Median / $compSt.Median
    $gainMean = $naiveSt.Mean / $compSt.Mean

    Write-Host ("`n  Конфігурація workers = {0} (n = {1}):" -f $wOpt, $BenchN) -ForegroundColor Green
    Write-Host ("    ДО оптимізації (kernel=naive)     : runs=[{0:F4}s, {1:F4}s, {2:F4}s] | median={3:F4}s | mean={4:F4}s | spread={5:F4}s ({6:F2}%) | value={7:F10}" -f `
        $naiveRuns[0], $naiveRuns[1], $naiveRuns[2], $naiveSt.Median, $naiveSt.Mean, $naiveSt.SpreadAbs, $naiveSt.SpreadPct, $naiveVal)
    Write-Host ("    ПІСЛЯ оптимізації (kernel=composite): runs=[{0:F4}s, {1:F4}s, {2:F4}s] | median={3:F4}s | mean={4:F4}s | spread={5:F4}s ({6:F2}%) | value={7:F10}" -f `
        $compRuns[0], $compRuns[1], $compRuns[2], $compSt.Median, $compSt.Mean, $compSt.SpreadAbs, $compSt.SpreadPct, $compVal)
    Write-Host ("    Ефект оптимізації (workers={0})    : прискорення за медіаною = {1:F3}x ({2:F4}s -> {3:F4}s), за середнім = {4:F3}x ({5:F4}s -> {6:F4}s)" -f `
        $wOpt, $gainMed, $naiveSt.Median, $compSt.Median, $gainMean, $naiveSt.Mean, $compSt.Mean)
}

Write-Host "`n==============================================================================" -ForegroundColor Cyan
Write-Host " 4. СПІЛЬНА СУМА ТА ЛОКАЛЬНІ РЕЗУЛЬТАТИ: none vs lock vs local (РПО О2.4)" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

foreach ($mode in @("none", "lock", "local")) {
    foreach ($w in @(1, 2, 4)) {
        $rm = Invoke-Compute -a 0.0 -b 60.0 -n 10000000 -workers $w -mode $mode -kernel "composite"
        Write-Host ("mode={0,-5} | workers={1} | value={2,14:F6} | ref={3,14:F6} | abs_err={4:E4} | wall_s={5:F4}s" -f `
            $mode, $w, $rm.value, $rm.reference, $rm.abs_error, $rm.wall_s)
    }
}
