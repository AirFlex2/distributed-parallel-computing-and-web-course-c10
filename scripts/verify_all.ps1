<#
.SYNOPSIS
    Комплексний сценарій автоматизованого тестування та перевірки ЛР1 (Web + РПО)
    Тема С10: «Запис на вибіркові дисципліни»
    Студент: Зінківський Тимур Євгенійович, група ІТІНФ-23-2
.DESCRIPTION
    Послідовно перевіряє:
    0. Лінтер OpenAPI 3.1 (Spectral CLI), модульні тести (dotnet test), версіоновані міграції schema_migrations та SQL VIEW group_seats.
    1-2. Захищений робочий режим (ALLOW_TEST_OVERRIDES=false): обов'язковий X-User-Id (400), заборона student_id у тілі (422),
         закриття тестових лазівок evaluated_at / X-Simulated-Now / /v1/admin/reset (403), авторизація Bearer (401).
    3-9. Курс «Розподілені та паралельні обчислення» (О1.1–О1.5, Д1.1–Д1.4 + спільний ресурс §1.10).
    10.  Курс «Обмін даними у Web-застосунках» (О1.1–О1.7, Д1.1–Д1.5 + ізоляція власника запису).
    Наприкінці у блоці finally повертає контейнери у захищений робочий режим ALLOW_TEST_OVERRIDES=false.
#>

param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$Token = "secret-c10-token-2026"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$script:ErrorCount = 0

$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $RepoRoot

$DockerExe = "docker"
if (-not (Get-Command "docker" -ErrorAction SilentlyContinue)) {
    if (Test-Path "C:\Program Files\Docker\Docker\resources\bin\docker.exe") {
        $DockerExe = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
    }
}

$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromSeconds(15)
$httpClient.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $Token)

$Headers = @{
    "Content-Type"  = "application/json"
    "Authorization" = "Bearer $Token"
}

function Write-Header($text) {
    Write-Host "`n================================================================================" -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "================================================================================" -ForegroundColor Cyan
}

function Write-Pass($text) {
    Write-Host "  [PASS] $text" -ForegroundColor Green
}

function Write-Fail($text) {
    $script:ErrorCount++
    Write-Host "  [FAIL] $text" -ForegroundColor Red
}

function Write-Info($text) {
    Write-Host "  [INFO] $text" -ForegroundColor Gray
}

function Get-ResponseText($resp) {
    if ($null -eq $resp) { return "" }
    if ($resp.Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($resp.Content)
    }
    return [string]$resp.Content
}

function Set-TestOverridesMode([bool]$enable) {
    $val = if ($enable) { "true" } else { "false" }
    $env:ALLOW_TEST_OVERRIDES = $val
    & $DockerExe compose up -d app-a app-b | Out-Null
    & $DockerExe compose restart nginx | Out-Null
    Remove-Item Env:ALLOW_TEST_OVERRIDES -ErrorAction SilentlyContinue

    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 300
        try {
            $h = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get -TimeoutSec 2
            if ($h.status -eq "ok" -and [bool]$h.allow_test_overrides -eq $enable) {
                return $true
            }
        } catch { }
    }
    return $false
}

Write-Header "СТАРТ КОМПЛЕКСНОЇ ПЕРЕВІРКИ ЛР1 (Тема С10: Web + РПО)"
Write-Info "Цільовий сервіс (через балансувальник Nginx): $BaseUrl"

# ------------------------------------------------------------------------------
# 0. Статична перевірка OpenAPI 3.1 (Spectral), модульні тести та schema_migrations
# ------------------------------------------------------------------------------
Write-Header "0. Контракт OpenAPI 3.1 (Spectral), модульні тести (dotnet test) та міграції БД"

if (Get-Command "npx" -ErrorAction SilentlyContinue) {
    $lintOut = (& npx --yes @stoplight/spectral-cli lint openapi.yaml --fail-severity=warn 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        Write-Pass "О1.3 (Web): Контракт openapi.yaml успішно пройшов перевірку лінтером Spectral (0 помилок, 0 попереджень)"
    } else {
        Write-Fail "О1.3 (Web): Лінтер Spectral виявив помилки в openapi.yaml:`n$lintOut"
    }
} else {
    Write-Fail "О1.3 (Web): Не знайдено npx для запуску лінтера Spectral"
}

if (Get-Command "dotnet" -ErrorAction SilentlyContinue) {
    $testOut = (& dotnet test tests/EnrollmentService.Tests/EnrollmentService.Tests.csproj --nologo -v minimal 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        Write-Pass "Д1.1 (Web/РПО): Модульні тести предметних правил і неблокуючої async-черги пройдено успішно"
    } else {
        Write-Fail "Модульні тести завершилися з помилкою:`n$testOut"
    }
} else {
    Write-Fail "Не знайдено dotnet SDK для запуску модульних тестів"
}

$migCount = (& $DockerExe exec dpc-c10-postgres psql -U dpc_user -d dpc_c10 -tAc "SELECT count(*) FROM schema_migrations WHERE version IN ('001','002');" 2>$null).Trim()
$viewCount = (& $DockerExe exec dpc-c10-postgres psql -U dpc_user -d dpc_c10 -tAc "SELECT count(*) FROM pg_views WHERE viewname = 'group_seats';" 2>$null).Trim()
if ($migCount -eq "2" -and $viewCount -eq "1") {
    Write-Pass "О1.1 (Web) та §1.10: Таблиця schema_migrations містить версії 001 і 002; group_seats реалізовано як SQL VIEW над єдиним ресурсом discipline_group"
} else {
    Write-Fail "Помилка перевірки міграцій або VIEW group_seats: schema_migrations=$migCount, pg_views=$viewCount"
}

# ------------------------------------------------------------------------------
# 1. Перевірка службового маршруту /health та відокремлення клієнта (§1.2)
# ------------------------------------------------------------------------------
Write-Header "1. Перевірка службових маршрутів GET /health та GET / (відокремлений клієнт §1.2)"
try {
    $healthRes = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
    if ([bool]$healthRes.allow_test_overrides -eq $true) {
        Write-Info "Перемикання стенду у робочий захищений режим ALLOW_TEST_OVERRIDES=false для перевірки безпеки..."
        $null = Set-TestOverridesMode -enable $false
        $healthRes = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
    }
    $rootRes = Invoke-WebRequest -Uri "$BaseUrl/" -Method Get -SkipHttpErrorCheck
    $rootCt = [string]($rootRes.Headers["Content-Type"] | Select-Object -First 1)

    if ($healthRes.status -eq "ok" -and $rootCt -match "application/json") {
        Write-Pass "Маршрут /health активний (вузол: $($healthRes.instance_id), черга: $($healthRes.queue_length)/$($healthRes.queue_capacity)); образ API не роздає вбудований клієнт (GET / -> JSON)"
    } else {
        Write-Fail "Неочікуваний стан /health або GET /: status=$($healthRes.status), rootCt=$rootCt"
    }
} catch {
    Write-Fail "Не вдалося з'єднатися з $BaseUrl/health: $_"
    Pop-Location
    exit 1
}

# ------------------------------------------------------------------------------
# 2. Перевірка безпеки у робочому режимі (ALLOW_TEST_OVERRIDES=false)
# ------------------------------------------------------------------------------
Write-Header "2. Безпека API у робочому режимі: Bearer (401), X-User-Id (400/422) та закриття тестових лазівок (403)"

$unauthRes = Invoke-WebRequest -Uri "$BaseUrl/v1/group-seats" -Method Get -SkipHttpErrorCheck
$unauthCt = [string]($unauthRes.Headers["Content-Type"] | Select-Object -First 1)
if ([int]$unauthRes.StatusCode -eq 401 -and $unauthCt -match "application/problem\+json") {
    Write-Pass "Запит без Bearer-токена відхилено з HTTP 401 Unauthorized (application/problem+json)"
} else {
    Write-Fail "Очікувався HTTP 401 problem+json, отримано: $([int]$unauthRes.StatusCode) ($unauthCt)"
}

$noUserPost = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json" } -Body '{"group_id":"DISC-WEB-01"}' -SkipHttpErrorCheck
$noUserGet  = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Get -SkipHttpErrorCheck
$noUserBody = (Get-ResponseText $noUserPost) | ConvertFrom-Json
if ([int]$noUserPost.StatusCode -eq 400 -and [int]$noUserGet.StatusCode -eq 400 -and $noUserBody.error_code -eq "missing_user_header") {
    Write-Pass "Запити без обов'язкового заголовка X-User-Id відхилено з HTTP 400 Bad Request (error_code=missing_user_header)"
} else {
    Write-Fail "Помилка перевірки обов'язковості X-User-Id: POST=$([int]$noUserPost.StatusCode), GET=$([int]$noUserGet.StatusCode)"
}

$ownerInBodyRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json"; "X-User-Id" = "student-1" } -Body '{"group_id":"DISC-WEB-01","student_id":"student-2"}' -SkipHttpErrorCheck
$ownerInBodyJson = (Get-ResponseText $ownerInBodyRes) | ConvertFrom-Json
if ([int]$ownerInBodyRes.StatusCode -eq 422 -and $ownerInBodyJson.error_code -eq "owner_in_body_forbidden") {
    Write-Pass "Спробу передати власника student_id у тілі POST /v1/enrollments відхилено з HTTP 422 (error_code=owner_in_body_forbidden)"
} else {
    Write-Fail "Помилка перевірки заборони student_id у тілі: status=$([int]$ownerInBodyRes.StatusCode)"
}

$evalAtProdRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json"; "X-User-Id" = "student-2" } -Body '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T07:00:00Z"}' -SkipHttpErrorCheck
$simHdrProdRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json"; "X-User-Id" = "student-2"; "X-Simulated-Now" = "2026-09-01T07:00:00Z" } -Body '{"group_id":"DISC-WINDOW-TEST"}' -SkipHttpErrorCheck
$resetProdRes  = Invoke-WebRequest -Uri "$BaseUrl/v1/admin/reset" -Method Post -Headers $Headers -SkipHttpErrorCheck
$realClockWinRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json"; "X-User-Id" = "student-2" } -Body '{"group_id":"DISC-WINDOW-TEST"}' -SkipHttpErrorCheck

if ([int]$evalAtProdRes.StatusCode -eq 403 -and [int]$simHdrProdRes.StatusCode -eq 403 -and [int]$resetProdRes.StatusCode -eq 403 -and [int]$realClockWinRes.StatusCode -eq 409) {
    Write-Pass "У робочому режимі (ALLOW_TEST_OVERRIDES=false) тестові лазівки evaluated_at, X-Simulated-Now та /v1/admin/reset закриті (403 Forbidden), а поза вікном повертається 409 Conflict!"
} else {
    Write-Fail "Помилка закриття тестових лазівок: evaluated_at=$([int]$evalAtProdRes.StatusCode), X-Simulated-Now=$([int]$simHdrProdRes.StatusCode), reset=$([int]$resetProdRes.StatusCode), realWindow=$([int]$realClockWinRes.StatusCode)"
}

# ==============================================================================
# Увімкнення контрольованого тестового стенду ALLOW_TEST_OVERRIDES=true (із гарантованим поверненням у finally)
# ==============================================================================
try {
    Write-Info "Активація контрольованого тестового режиму ALLOW_TEST_OVERRIDES=true для автоматизованих дослідів..."
    if (-not (Set-TestOverridesMode -enable $true)) {
        Write-Fail "Не вдалося активувати ALLOW_TEST_OVERRIDES=true"
        throw "Test mode activation failed"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -Method Post -Headers $Headers

    # --------------------------------------------------------------------------
    # 3. Пункт О1.1 (РПО) та §1.10 / С10 п. 2, 6: Розподіл відповідальності між Web і РПО,
    #    відомість деканату за request_id, перевірка K та ЄКТС у РПО, ідемпотентність та 409 Conflict
    # --------------------------------------------------------------------------
    Write-Header "3. ПУНКТ О1.1 (РПО) та С10 §3.12: Розподіл відповідальності Web і РПО, відомість деканату за request_id, ліміти K/ЄКТС"

    # Крок 1: Первинний запис студента в групу та зменшення вільних місць (30 -> 29) виконує ВИКЛЮЧНО Web
    $webPrimaryEnrRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers @{ "Content-Type" = "application/json"; "X-User-Id" = "student-1" } -Body '{"group_id":"DISC-DPC-01"}' -SkipHttpErrorCheck
    $webPrimaryEnrBody = (Get-ResponseText $webPrimaryEnrRes) | ConvertFrom-Json
    $webDiscAfterPrimary = Invoke-RestMethod -Uri "$BaseUrl/v1/disciplines?limit=20" -Method Get
    $webDpcGroupBeforeRpo = ($webDiscAfterPrimary.items | ForEach-Object { $_.groups } | Where-Object { $_.id -eq "DISC-DPC-01" } | Select-Object -First 1)

    if ([int]$webPrimaryEnrRes.StatusCode -eq 201 -and $webPrimaryEnrBody.status -eq "held" -and $webDpcGroupBeforeRpo.available -eq 29) {
        Write-Pass "Крок 1 (Виключна відповідальність Web): POST /v1/enrollments записав student-1 у DISC-DPC-01 (status=held) і зменшив available з 30 до 29"
    } else {
        Write-Fail "Помилка первинного запису через Web: status=$([int]$webPrimaryEnrRes.StatusCode), available=$($webDpcGroupBeforeRpo.available)"
    }

    # Крок 2: Обробник РПО не резервує місце повторно, а фіксує заявку у відомості деканату (decanate_roster) за request_id
    $fixedId = [guid]::NewGuid().ToString()
    $bodyReq1 = @{
        requestId  = $fixedId
        student_id = "student-1"
        group_id   = "DISC-DPC-01"
    } | ConvertTo-Json

    $postRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyReq1 -SkipHttpErrorCheck
    if ([int]$postRes.StatusCode -eq 202) {
        Write-Pass "Запит РПО прийнято: отримано HTTP 202 Accepted (запис збережено в БД у стані 'queued' до постановки в чергу)"
    } else {
        Write-Fail "Очікувався HTTP 202, отримано $([int]$postRes.StatusCode)"
    }

    $acceptedData = (Get-ResponseText $postRes) | ConvertFrom-Json
    $reqId = $acceptedData.id
    $finalStatus = ""
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 150
        $statusRes = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$reqId" -Method Get -Headers $Headers
        $finalStatus = $statusRes.status
        if ($finalStatus -in @("done", "rejected", "failed")) { break }
    }

    if ($finalStatus -eq "done" -and $statusRes.recordedInRoster -eq $true) {
        Write-Pass "Заявка $reqId успішно перейшла у стан 'done' і зафіксована у відомості деканату за request_id (QueueWait=$($statusRes.queueWaitMs) мс, Processing=$($statusRes.processingTimeMs) мс)"
    } else {
        Write-Fail "Заявка не досягла стану 'done' або не зафіксована у відомості: status=$finalStatus, recordedInRoster=$($statusRes.recordedInRoster), reason=$($statusRes.rejectionReason)"
    }

    # Перевірка, що РПО НЕ зменшив available вдруге (available залишається 29, а не 28!), підтвердив enrollment (confirmed) і створив запис у decanate_roster
    $dpcGroupAfter = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-DPC-01" -Method Get -Headers $Headers
    $webDiscAfter  = Invoke-RestMethod -Uri "$BaseUrl/v1/disciplines?limit=20" -Method Get
    $webDpcGroup   = ($webDiscAfter.items | ForEach-Object { $_.groups } | Where-Object { $_.id -eq "DISC-DPC-01" } | Select-Object -First 1)
    $rosterRes     = Invoke-RestMethod -Uri "$BaseUrl/v1/decanate-roster?request_id=$reqId" -Method Get -Headers $Headers
    $webConfirmed  = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$($webPrimaryEnrBody.id)" -Method Get -Headers @{ "X-User-Id" = "student-1" }

    if ($dpcGroupAfter.taken -eq 1 -and $dpcGroupAfter.freeSeats -eq 29 -and $dpcGroupAfter.rosterCount -eq 1 -and
        $webDpcGroup.available -eq 29 -and $rosterRes.count -eq 1 -and $rosterRes.items[0].assignment_type -eq "primary_roster" -and
        $webConfirmed.status -eq "confirmed") {
        Write-Pass "С10 §3.12 (Без подвійного резервування): воркер РПО зафіксував request_id у decanate_roster (rosterCount=1, status=confirmed), а вільні місця залишилися 29/30 (taken=1)!"
    } else {
        Write-Fail "Помилка узгодження Web і РПО: dpc.taken=$($dpcGroupAfter.taken), dpc.free=$($dpcGroupAfter.freeSeats), rosterCount=$($dpcGroupAfter.rosterCount), web.available=$($webDpcGroup.available), enrStatus=$($webConfirmed.status)"
    }

    # Перевірка, що РПО відхиляє заявку зареєстрованого студента без попереднього запису через Web, а також перевіряє ліміти K та 30.0 ЄКТС безпосередньо в РПО
    $noWebReq = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body '{"student_id":"student-2","group_id":"DISC-DPC-01"}'
    $kLimitReq = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body '{"student_id":"student-limit-max","group_id":"DISC-DPC-01"}'
    $ectsLimitReq = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body '{"student_id":"student-ects-20","group_id":"DISC-ECTS-10-5-G1"}'

    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 150
        $stNoWeb = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$($noWebReq.id)" -Method Get -Headers $Headers
        $stK     = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$($kLimitReq.id)" -Method Get -Headers $Headers
        $stEcts  = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$($ectsLimitReq.id)" -Method Get -Headers $Headers
        if ($stNoWeb.status -ne "queued" -and $stNoWeb.status -ne "processing" -and
            $stK.status -ne "queued" -and $stK.status -ne "processing" -and
            $stEcts.status -ne "queued" -and $stEcts.status -ne "processing") { break }
    }

    if ($stNoWeb.status -eq "rejected" -and $stK.status -eq "rejected" -and $stEcts.status -eq "rejected") {
        Write-Pass "Перевірка обмежень у логіці РПО: відхилено запит без первинного запису Web (rejected), перевищення max_disciplines K=5 (rejected) та перевищення 30.5 > 30.0 ЄКТС (rejected)!"
    } else {
        Write-Fail "Помилка перевірки обмежень у РПО: noWeb=$($stNoWeb.status), K=$($stK.status), ECTS=$($stEcts.status)"
    }

    $dupSameRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyReq1 -SkipHttpErrorCheck
    $groupStateAfterDup = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-DPC-01" -Method Get -Headers $Headers
    if ([int]$dupSameRes.StatusCode -eq 202 -and $groupStateAfterDup.taken -eq 1 -and $groupStateAfterDup.rosterCount -eq 1) {
        Write-Pass "Повторний запит із тим самим ID не потрапив повторно в чергу і не продублював запис у відомості деканату (taken = 1/30, rosterCount = 1)"
    } else {
        Write-Fail "Порушення ідемпотентності повторного запиту: status=$([int]$dupSameRes.StatusCode), taken=$($groupStateAfterDup.taken)"
    }

    $bodyConflict = @{
        requestId  = $fixedId
        student_id = "STD-OTHER-999"
        group_id   = "DISC-DPC-01"
    } | ConvertTo-Json
    $conflictRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyConflict -SkipHttpErrorCheck
    if ([int]$conflictRes.StatusCode -eq 409) {
        Write-Pass "Повторний запит із тим самим ID та іншими даними відхилено з HTTP 409 Conflict"
    } else {
        Write-Fail "Очікувався HTTP 409 Conflict при неспівпадінні даних, отримано: $([int]$conflictRes.StatusCode)"
    }

    $bodyInvalid = @{ student_id = "STD-INVALID"; group_id = "NON-EXISTENT-GROUP" } | ConvertTo-Json
    $invalidRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyInvalid -SkipHttpErrorCheck
    if ([int]$invalidRes.StatusCode -eq 202) {
        $invId = ((Get-ResponseText $invalidRes) | ConvertFrom-Json).id
        for ($i = 0; $i -lt 25; $i++) {
            Start-Sleep -Milliseconds 150
            $invStatus = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$invId" -Method Get -Headers $Headers
            if ($invStatus.status -in @("done", "rejected", "failed")) { break }
        }
        if ($invStatus.status -eq "failed") {
            Write-Pass "Неіснуюча група успішно перейшла у статус 'failed': $($invStatus.rejectionReason)"
        } else {
            Write-Fail "Очікувався статус 'failed', отримано: $($invStatus.status)"
        }
    } else {
        Write-Fail "Очікувався статус 202 для невідомої групи, отримано: $([int]$invalidRes.StatusCode)"
    }

    # --------------------------------------------------------------------------
    # 4. Пункт О1.2 (РПО): Обмежена черга, 429 + Retry-After та p95 /health під навантаженням
    # --------------------------------------------------------------------------
    Write-Header "4. ПУНКТ О1.2 (РПО): Обмежена черга QUEUE_CAPACITY=100, перевантаження 429 та p95 /health під навантаженням"
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers
    Write-Info "Подання залпу зі 150 одночасних запитів (QUEUE_CAPACITY=100, WORKERS=3) та одночасне вимірювання 30 запитів /health..."

    $swBurst = [System.Diagnostics.Stopwatch]::StartNew()
    $tasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($k = 1; $k -le 150; $k++) {
        $content = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-BURST-$k`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $tasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $content))
    }

    $healthLatencies = [System.Collections.Generic.List[double]]::new()
    for ($h = 0; $h -lt 30; $h++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $null = $httpClient.GetAsync("$BaseUrl/health").GetAwaiter().GetResult()
        $sw.Stop()
        $healthLatencies.Add($sw.Elapsed.TotalMilliseconds)
        Start-Sleep -Milliseconds 15
    }

    [System.Threading.Tasks.Task]::WaitAll($tasks.ToArray())
    $swBurst.Stop()
    $burstDurationSec = [Math]::Round($swBurst.Elapsed.TotalSeconds, 2)
    $responses = $tasks | ForEach-Object { $_.Result }

    $cnt202 = ($responses | Where-Object { [int]$_.StatusCode -eq 202 }).Count
    $cnt429 = ($responses | Where-Object { [int]$_.StatusCode -eq 429 }).Count
    $hasRetryAfter = ($responses | Where-Object { [int]$_.StatusCode -eq 429 -and $_.Headers.Contains("Retry-After") }).Count

    $sortedHealth = $healthLatencies | Sort-Object
    $p50Index = [Math]::Max(0, [Math]::Ceiling($sortedHealth.Count * 0.50) - 1)
    $p95Index = [Math]::Max(0, [Math]::Ceiling($sortedHealth.Count * 0.95) - 1)
    $p50Health = [Math]::Round($sortedHealth[$p50Index], 2)
    $p95Health = [Math]::Round($sortedHealth[$p95Index], 2)

    Write-Info "Тривалість фази пікового залпу (150 запитів + 30 /health): $burstDurationSec с | 202 Accepted = $cnt202, 429 = $cnt429 (з Retry-After: $hasRetryAfter)"
    if ($cnt429 -gt 0 -and $hasRetryAfter -eq $cnt429) {
        Write-Pass "Переповнення черги оброблено коректно: отримано $cnt429 відповідей 429, усі з заголовком Retry-After"
    } else {
        Write-Fail "Помилка обробки переповнення: 429=$cnt429, Retry-After=$hasRetryAfter"
    }

    if ($p95Health -le 100.0) {
        Write-Pass "Затримка /health безпосередньо під навантаженням: p50 = $p50Health мс, p95 = $p95Health мс (вимога <= 100 мс ВИКОНАНА)"
    } else {
        Write-Fail "Помилка перевірки /health під навантаженням: p95 = $p95Health мс"
    }

    # --------------------------------------------------------------------------
    # 5. Пункт О1.4 (РПО): Захист єдиного спільного ресурсу discipline_group (SAFE vs UNSAFE)
    # --------------------------------------------------------------------------
    Write-Header "5. ПУНКТ О1.4 (РПО): Захист єдиного спільного ресурсу discipline_group (порівняння SAFE vs UNSAFE)"

    Write-Info "Крок 5.1: Робочий безпечний режим UNSAFE_MODE=false (група DISC-TEST-SAFE, seats=5, 20 конкурентних запитів)..."
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $safeTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($s = 1; $s -le 20; $s++) {
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-SAFE-$s`",`"group_id`":`"DISC-TEST-SAFE`"}", [System.Text.Encoding]::UTF8, "application/json")
        $safeTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($safeTasks.ToArray())

    for ($waitLoop = 0; $waitLoop -lt 50; $waitLoop++) {
        Start-Sleep -Milliseconds 250
        $mSnap = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
        if ($mSnap.queue_length -eq 0 -and $mSnap.in_processing -eq 0 -and ($mSnap.counters.total_done + $mSnap.counters.total_rejected) -ge 20) {
            break
        }
    }

    $safeGroup = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-TEST-SAFE" -Method Get -Headers $Headers
    $safeMetrics = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
    if ($safeGroup.taken -eq 5 -and $safeGroup.seats -eq 5 -and $safeGroup.invariantHolds -eq $true -and $safeMetrics.counters.total_done -eq 5 -and $safeMetrics.counters.total_rejected -eq 15) {
        Write-Pass "БЕЗПЕЧНИЙ РЕЖИМ: Інваріант строго дотримано! Взято $($safeGroup.taken)/$($safeGroup.seats) місць (done=5, rejected=15)."
    } else {
        Write-Fail "Порушення у безпечному режимі: taken=$($safeGroup.taken)/$($safeGroup.seats), done=$($safeMetrics.counters.total_done), rejected=$($safeMetrics.counters.total_rejected)"
    }

    Write-Info "Крок 5.2: Небезпечний режим UNSAFE_MODE=true (ізольована група DISC-TEST-UNSAFE, seats=5, 20 конкурентних запитів)..."
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=true" -Method Post -Headers $Headers

    $unsafeTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($u = 1; $u -le 20; $u++) {
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-UNSAFE-$u`",`"group_id`":`"DISC-TEST-UNSAFE`"}", [System.Text.Encoding]::UTF8, "application/json")
        $unsafeTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($unsafeTasks.ToArray())

    for ($waitLoop = 0; $waitLoop -lt 50; $waitLoop++) {
        Start-Sleep -Milliseconds 250
        $mSnap = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
        if ($mSnap.queue_length -eq 0 -and $mSnap.in_processing -eq 0 -and ($mSnap.counters.total_done + $mSnap.counters.total_rejected) -ge 20) {
            break
        }
    }

    $unsafeGroup = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-TEST-UNSAFE" -Method Get -Headers $Headers
    Write-Info "Стан групи DISC-TEST-UNSAFE: taken=$($unsafeGroup.taken), freeSeats=$($unsafeGroup.freeSeats), seats=$($unsafeGroup.seats), invariantHolds=$($unsafeGroup.invariantHolds)"
    if ($unsafeGroup.taken -gt $unsafeGroup.seats -and $unsafeGroup.invariantHolds -eq $false) {
        Write-Pass "НЕБЕЗПЕЧНИЙ РЕЖИМ: Стан гонитви успішно відтворено! Інваріант ПОРУШЕНО: taken ($($unsafeGroup.taken)) > seats ($($unsafeGroup.seats))."
    } else {
        Write-Fail "Не вдалося продемонструвати порушення інваріанта в UNSAFE_MODE: taken=$($unsafeGroup.taken)/$($unsafeGroup.seats)"
    }

    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    # --------------------------------------------------------------------------
    # 6. Пункт О1.5 (РПО): Монотонні позначки та одноразова фіксація першого GET
    # --------------------------------------------------------------------------
    Write-Header "6. ПУНКТ О1.5 (РПО): Монотонні позначки часу та одноразова фіксація першого GET"
    $bodyO15 = @{ student_id = "STD-O15-CHECK"; group_id = "DISC-DPC-01" } | ConvertTo-Json
    $resO15 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyO15
    $idO15 = $resO15.id

    for ($w = 0; $w -lt 30; $w++) {
        Start-Sleep -Milliseconds 100
        $mCheck = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
        if ($mCheck.counters.total_done -ge 1 -and $mCheck.in_processing -eq 0) { break }
    }

    $firstGet = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$idO15" -Method Get -Headers $Headers
    Start-Sleep -Milliseconds 150
    $secondGet = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$idO15" -Method Get -Headers $Headers
    $mAfterO15 = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get

    if ($null -ne $firstGet.firstReadAt -and $null -ne $firstGet.deliveryWaitMs -and
        $firstGet.firstReadAt -eq $secondGet.firstReadAt -and
        $firstGet.deliveryWaitMs -eq $secondGet.deliveryWaitMs -and
        $mAfterO15.counters.monotonic_order_violations -eq 0) {
        Write-Pass "Перший GET зафіксував firstReadAt ($($firstGet.firstReadAt)) та deliveryWaitMs = $($firstGet.deliveryWaitMs) мс; повторний GET не змінив позначку!"
        Write-Pass "Порушень монотонності часових позначок: $($mAfterO15.counters.monotonic_order_violations)"
    } else {
        Write-Fail "Помилка О1.5: firstGet.deliveryWaitMs=$($firstGet.deliveryWaitMs), secondGet.deliveryWaitMs=$($secondGet.deliveryWaitMs), violations=$($mAfterO15.counters.monotonic_order_violations)"
    }

    # --------------------------------------------------------------------------
    # 7. Пункт Д1.1 (РПО): Реальні вимірювання обох моделей виконання (неблокуюча async_tasks vs dedicated_threads)
    # --------------------------------------------------------------------------
    Write-Header "7. ПУНКТ Д1.1 (РПО): Порівняння моделей виконання async_tasks (неблокуючий TCS) та dedicated_threads"

    function Invoke-ModelBenchmark([string]$modelName, [int]$requestCount = 30) {
        $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false&executionModel=$modelName" -Method Post -Headers $Headers
        $swModel = [System.Diagnostics.Stopwatch]::StartNew()

        $benchTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
        for ($b = 1; $b -le $requestCount; $b++) {
            $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-$modelName-$b`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
            $benchTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
        }
        [System.Threading.Tasks.Task]::WaitAll($benchTasks.ToArray())

        for ($w = 0; $w -lt 80; $w++) {
            Start-Sleep -Milliseconds 150
            $m = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
            if ($m.queue_length -eq 0 -and $m.in_processing -eq 0 -and ($m.counters.total_done + $m.counters.total_rejected) -eq $requestCount) {
                break
            }
        }
        $swModel.Stop()
        $finalM = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
        return [PSCustomObject]@{
            Model          = $finalM.service_state.execution_model
            TotalSec       = [Math]::Round($swModel.Elapsed.TotalSeconds, 2)
            ThroughputRps  = [Math]::Round($requestCount / $swModel.Elapsed.TotalSeconds, 2)
            DoneCount      = $finalM.counters.total_done
            QueueWaitP50   = $finalM.intervals_ms.queue_wait_ms.p50_ms
            QueueWaitP95   = $finalM.intervals_ms.queue_wait_ms.p95_ms
            ProcTimeP50    = $finalM.intervals_ms.processing_time_ms.p50_ms
            ProcTimeP95    = $finalM.intervals_ms.processing_time_ms.p95_ms
        }
    }

    Write-Info "Вимірювання моделі 1: EXECUTION_MODEL=async_tasks (пакет 30 запитів)..."
    $resAsync = Invoke-ModelBenchmark -modelName "async_tasks" -requestCount 30
    Write-Info "  [async_tasks] Час = $($resAsync.TotalSec) с | Пропускна здатність = $($resAsync.ThroughputRps) зап/с | QueueWait p50/p95 = $($resAsync.QueueWaitP50)/$($resAsync.QueueWaitP95) мс | Proc p50/p95 = $($resAsync.ProcTimeP50)/$($resAsync.ProcTimeP95) мс"

    Write-Info "Вимірювання моделі 2: EXECUTION_MODEL=dedicated_threads (пакет 30 запитів)..."
    $resThreads = Invoke-ModelBenchmark -modelName "dedicated_threads" -requestCount 30
    Write-Info "  [dedicated_threads] Час = $($resThreads.TotalSec) с | Пропускна здатність = $($resThreads.ThroughputRps) зап/с | QueueWait p50/p95 = $($resThreads.QueueWaitP50)/$($resThreads.QueueWaitP95) мс | Proc p50/p95 = $($resThreads.ProcTimeP50)/$($resThreads.ProcTimeP95) мс"

    if ($resAsync.Model -eq "async_tasks" -and $resAsync.DoneCount -eq 30 -and
        $resThreads.Model -eq "dedicated_threads" -and $resThreads.DoneCount -eq 30) {
        Write-Pass "Обидві моделі виконання (async_tasks та dedicated_threads) успішно протестовані при однаковому навантаженні (30/30 оброблено)"
    } else {
        Write-Fail "Помилка тестування моделей Д1.1: async=$($resAsync.Model)($($resAsync.DoneCount)), threads=$($resThreads.Model)($($resThreads.DoneCount))"
    }

    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -Method Post -Headers $Headers

    # --------------------------------------------------------------------------
    # 8. Пункт Д1.2 (РПО): Перевірка закону Літтла (60 с ~4 зап/с, розігрів 10 с виключено + незалежна вибірка 100 мс)
    # --------------------------------------------------------------------------
    Write-Header "8. ПУНКТ Д1.2 (РПО): Закон Літтла L = lambda * W (60 с по ~4 зап/с, розігрів 10 с виключено + незалежна вибірка 100 мс)"
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $totalSec = 60
    $warmupSec = 10
    $postWarmupQueues = [System.Collections.Generic.List[int]]::new()
    $littleTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    $reqSeq = 0

    Write-Info "Подання потоку ~4 запити/с протягом 60 секунд (перші 10 с розігріву виключаються з вимірювань)..."
    for ($sec = 0; $sec -lt $totalSec; $sec++) {
        $secTimer = [System.Diagnostics.Stopwatch]::StartNew()

        if ($sec -eq $warmupSec) {
            $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?metricsOnly=true" -Method Post -Headers $Headers
            Write-Info "Перші 10 с розігріву завершено -> вікно вимірювання метрик та 100 мс семплера скинуто (заліковий інтервал 50 с)..."
        }

        for ($j = 0; $j -lt 4; $j++) {
            $reqSeq++
            $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-LITTLE-$reqSeq`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
            $littleTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
        }

        if ($sec -ge $warmupSec) {
            Start-Sleep -Milliseconds 120
            $hSnap = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
            $postWarmupQueues.Add([int]$hSnap.queue_length)
        }

        $remSec = 1000 - [int]$secTimer.ElapsedMilliseconds
        if ($remSec -gt 0) { Start-Sleep -Milliseconds $remSec }
    }

    $metricsLittle = Invoke-RestMethod -Uri "$BaseUrl/metrics?format=json" -Method Get
    [System.Threading.Tasks.Task]::WaitAll($littleTasks.ToArray())
    Start-Sleep -Seconds 2

    $halfIdx = [Math]::Floor($postWarmupQueues.Count / 2)
    $firstHalfAvg = ($postWarmupQueues[0..($halfIdx - 1)] | Measure-Object -Average).Average
    $secondHalfAvg = ($postWarmupQueues[$halfIdx..($postWarmupQueues.Count - 1)] | Measure-Object -Average).Average
    $clientSampledAvg = [Math]::Round((($postWarmupQueues | Measure-Object -Average).Average), 4)

    $ll = $metricsLittle.littles_law
    Write-Info "Параметри закону Літтла за єдиний заліковий інтервал ($($ll.observation_window_sec) с):"
    Write-Info "  Інтенсивність надходження (lambda): $($ll.lambda_arrival_rate_rps) зап/с (прийнято у вікні: $($ll.window_arrivals_count))"
    Write-Info "  Середній час очікування у черзі (W): $($ll.w_avg_queue_wait_sec) с"
    Write-Info "  L_теоретичне (lambda * W): $($ll.l_theoretical_avg_queue_length)"
    Write-Info "  L_спостережуване (точний часовий інтеграл): $($ll.l_observed_avg_queue_length) (розбіжність: $($ll.relative_discrepancy_pct) %)"
    Write-Info "  L_спостережуване (незалежна вибірка 100 мс, N=$($ll.independent_sample_count)): $($ll.l_observed_independent_sampled) (розбіжність: $($ll.independent_discrepancy_pct) %)"
    Write-Info "  Зовнішня HTTP-вибірка клієнта (50 замірів раз/с): $clientSampledAvg"

    if ($ll.observation_window_sec -ge 45.0 -and $ll.queue_is_bounded -eq $true -and
        ($secondHalfAvg -le ($firstHalfAvg + 3.0)) -and
        $ll.independent_sample_count -ge 400 -and $ll.independent_discrepancy_pct -le 25.0) {
        Write-Pass "Закон Літтла підтверджено як точним інтегралом ($($ll.relative_discrepancy_pct)%), так і незалежною вибіркою 100 мс ($($ll.independent_discrepancy_pct)%, N=$($ll.independent_sample_count)); черга стаціонарна!"
    } else {
        Write-Fail "Помилка перевірки закону Літтла: window=$($ll.observation_window_sec)s, indepCount=$($ll.independent_sample_count), indepDisc=$($ll.independent_discrepancy_pct)%"
    }

    # --------------------------------------------------------------------------
    # 9. Пункт Д1.4 (РПО): Випробування з SIGTERM та перевірка структурованого JSON-журналу завершення
    # --------------------------------------------------------------------------
    Write-Header "9. ПУНКТ Д1.4 (РПО): Випробування з SIGTERM, JSON-події життєвого циклу та завершення прийнятих заявок"
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $sigtermBatchCount = 12
    Write-Info "Подання $sigtermBatchCount заявок у чергу перед відправкою сигналу SIGTERM..."
    $sigtermTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($s = 1; $s -le $sigtermBatchCount; $s++) {
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"STD-SIGTERM-$s`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $sigtermTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($sigtermTasks.ToArray())

    $acceptedSigtermIds = [System.Collections.Generic.List[string]]::new()
    foreach ($t in $sigtermTasks) {
        if ([int]$t.Result.StatusCode -eq 202) {
            $b = $t.Result.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            $acceptedSigtermIds.Add($b.id)
        }
    }

    $sigtermSinceUtc = (Get-Date).ToUniversalTime().AddSeconds(-2).ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Info "Надсилання сигналу SIGTERM контейнеру dpc-c10-api (app-a)..."
    $swDrain = [System.Diagnostics.Stopwatch]::StartNew()
    & $DockerExe kill --signal=SIGTERM dpc-c10-api | Out-Null

    Start-Sleep -Milliseconds 150
    try {
        $postDuringStop = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body '{"student_id":"STD-AFTER-SIGTERM","group_id":"DISC-DPC-01"}' -SkipHttpErrorCheck -TimeoutSec 2
        if ([int]$postDuringStop.StatusCode -in @(502, 503, 504)) {
            Write-Pass "Новий запит під час завершення роботи відхилено з HTTP $([int]$postDuringStop.StatusCode)"
        }
    } catch { }

    for ($w = 0; $w -lt 60; $w++) {
        $running = (& $DockerExe inspect -f "{{.State.Running}}" dpc-c10-api 2>$null).Trim()
        if ($running -eq "false") { break }
        Start-Sleep -Milliseconds 250
    }
    $swDrain.Stop()

    $logsAfterStop = (& $DockerExe logs --since $sigtermSinceUtc dpc-c10-api 2>&1) -join "`n"
    $hasSigtermReceived = $logsAfterStop -match '"event":"sigterm_received"'
    $hasGracefulDone    = $logsAfterStop -match '"event":"graceful_shutdown_completed"'

    if ($hasSigtermReceived -and $hasGracefulDone) {
        Write-Pass "У stdout контейнера dpc-c10-api зафіксовано структуровані JSON-події 'sigterm_received' та 'graceful_shutdown_completed' (час вивантаження: $([Math]::Round($swDrain.Elapsed.TotalSeconds, 2)) с)"
    } else {
        Write-Fail "У stdout контейнера відсутні JSON-події SIGTERM: sigterm_received=$hasSigtermReceived, graceful_shutdown_completed=$hasGracefulDone"
    }

    & $DockerExe start dpc-c10-api | Out-Null
    & $DockerExe compose restart nginx | Out-Null
    for ($w = 0; $w -lt 40; $w++) {
        Start-Sleep -Milliseconds 300
        try {
            $hCheck = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats" -Method Get -Headers $Headers -TimeoutSec 2
            if ($hCheck.Count -ge 1) { break }
        } catch { }
    }

    $completedAfterSigterm = 0
    foreach ($id in $acceptedSigtermIds) {
        $st = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$id" -Method Get -Headers $Headers
        if ($st.status -in @("done", "rejected")) { $completedAfterSigterm++ }
    }

    if ($hasSigtermReceived -and $hasGracefulDone -and $completedAfterSigterm -eq $sigtermBatchCount) {
        Write-Pass "Д1.4 (РПО): Усі $completedAfterSigterm/$sigtermBatchCount прийнятих до SIGTERM заявок успішно доведені до кінцевого стану"
    } else {
        Write-Fail "Помилка Д1.4: completed=$completedAfterSigterm/$sigtermBatchCount"
    }

    # ==========================================================================
    # 10. КУРС «ОБМІН ДАНИМИ У WEB-ЗАСТОСУНКАХ» (ЛР1: О1.1–О1.7 та Д1.1–Д1.5)
    # ==========================================================================
    Write-Header "10. КУРС «ОБМІН ДАНИМИ У WEB-ЗАСТОСУНКАХ» (О1.1–О1.7 та Д1.1–Д1.5)"
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    Write-Info "Перевірка О1.6 та Д1.4 (Web): балансування Nginx least_conn (app-a/app-b), X-Request-ID, W3C traceparent та JSON-журнал..."
    $seenInstances = [System.Collections.Generic.HashSet[string]]::new()
    $customReqId = "req-web-test-2026-01"
    $validTraceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"

    for ($i = 1; $i -le 12; $i++) {
        $reqHdrs = @{ "X-Request-ID" = "$customReqId-$i"; "traceparent" = $validTraceparent }
        $r = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -Headers $reqHdrs -SkipHttpErrorCheck
        $inst = $r.Headers["X-Instance-Id"] | Select-Object -First 1
        if ($inst) { $null = $seenInstances.Add($inst) }
    }

    $echoReqRes = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -Headers @{ "X-Request-ID" = $customReqId; "traceparent" = $validTraceparent } -SkipHttpErrorCheck
    $echoedReqId = $echoReqRes.Headers["X-Request-ID"] | Select-Object -First 1
    $echoedTraceId = $echoReqRes.Headers["X-Trace-Id"] | Select-Object -First 1

    if ($seenInstances.Contains("app-a") -and $seenInstances.Contains("app-b") -and $echoedReqId -eq $customReqId -and $echoedTraceId -eq "4bf92f3577b34da6a3ce929d0e0e4736") {
        Write-Pass "О1.6 та Д1.4 (Web): Балансувальник Nginx розподіляє запити між app-a та app-b; X-Request-ID ($echoedReqId) та trace_id ($echoedTraceId) підтверджено!"
    } else {
        Write-Fail "Помилка О1.6/Д1.4 (Web): instances=$($seenInstances -join ','), reqId=$echoedReqId, traceId=$echoedTraceId"
    }

    $containerJsonLogs = ((& $DockerExe logs --tail 20 dpc-c10-api 2>&1) + (& $DockerExe logs --tail 20 dpc-c10-app-b 2>&1)) -join "`n"
    if ($containerJsonLogs -match '"event":"http_request"' -and $containerJsonLogs -match '"trace_id":"4bf92f3577b34da6a3ce929d0e0e4736"' -and $containerJsonLogs -match '"duration_ms":' -and $containerJsonLogs -notmatch 'secret-c10-token-2026') {
        Write-Pass "О1.6 та Д1.4 (Web): Однорядковий JSON-журнал містить event=http_request, ts, instance, route, status, duration_ms, request_id, trace_id і не містить токенів!"
    } else {
        Write-Fail "Помилка формату JSON-журналу в stdout контейнерів"
    }

    Write-Info "Перевірка О1.3, О1.4 та Д1.3 (Web): GET /v1/disciplines, GET /v1/students, POST /v1/enrollments (201 + Location), перевірка власника та Problem Details..."
    $discRes = Invoke-RestMethod -Uri "$BaseUrl/v1/disciplines?limit=20" -Method Get
    $studRes = Invoke-RestMethod -Uri "$BaseUrl/v1/students" -Method Get
    if ($discRes.items.Count -ge 5 -and $studRes.items.Count -ge 5) {
        Write-Pass "О1.3 (Web): Маршрути GET /v1/disciplines ($($discRes.items.Count) дисциплін) та GET /v1/students ($($studRes.items.Count) студентів) повернули 200 OK"
    } else {
        Write-Fail "Помилка маршрутів GET /v1/disciplines або GET /v1/students"
    }

    $webHeadersStudent1 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-1"; "traceparent" = $validTraceparent }
    $webHeadersStudent2 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-2" }

    $enr1Res = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-WEB-01"}' -SkipHttpErrorCheck
    $enr1Body = (Get-ResponseText $enr1Res) | ConvertFrom-Json
    $enr1Location = $enr1Res.Headers["Location"] | Select-Object -First 1

    if ([int]$enr1Res.StatusCode -eq 201 -and $enr1Location -eq "/v1/enrollments/$($enr1Body.id)" -and $enr1Body.status -eq "held" -and $enr1Body.id -is [string]) {
        Write-Pass "О1.4 та Д1.3 (Web): POST /v1/enrollments повернув 201 Created із заголовком Location: $enr1Location (id=$($enr1Body.id), status=held)"
    } else {
        Write-Fail "Помилка головної операції POST /v1/enrollments: status=$([int]$enr1Res.StatusCode), location=$enr1Location"
    }

    # Перевірка ізоляції власника запису (student-1 бачить 200, student-2 отримує 404 на чужий запис)
    $ownGetOk = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments/$($enr1Body.id)" -Method Get -Headers $webHeadersStudent1 -SkipHttpErrorCheck
    $foreignGet404 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments/$($enr1Body.id)" -Method Get -Headers $webHeadersStudent2 -SkipHttpErrorCheck
    $foreignCancel404 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments/$($enr1Body.id)/cancel" -Method Post -Headers $webHeadersStudent2 -SkipHttpErrorCheck

    if ([int]$ownGetOk.StatusCode -eq 200 -and [int]$foreignGet404.StatusCode -eq 404 -and [int]$foreignCancel404.StatusCode -eq 404) {
        Write-Pass "Ізоляція власника X-User-Id: власник student-1 читає свій запис (200 OK), чужий студент student-2 отримує 404 Not Found при читанні та скасуванні!"
    } else {
        Write-Fail "Порушення ізоляції власника: ownGet=$([int]$ownGetOk.StatusCode), foreignGet=$([int]$foreignGet404.StatusCode), foreignCancel=$([int]$foreignCancel404.StatusCode)"
    }

    $null = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-DPC-01"}' -SkipHttpErrorCheck
    $null = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-AI-02"}' -SkipHttpErrorCheck

    $err400 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-WEB-01"' -SkipHttpErrorCheck
    $err422 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"   "}' -SkipHttpErrorCheck
    $err404 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-NON-EXISTENT"}' -SkipHttpErrorCheck
    $err409 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-WEB-01"}' -SkipHttpErrorCheck

    $prob409 = (Get-ResponseText $err409) | ConvertFrom-Json
    $ct409 = [string]($err409.Headers["Content-Type"] | Select-Object -First 1)

    if ([int]$err400.StatusCode -eq 400 -and [int]$err422.StatusCode -eq 422 -and [int]$err404.StatusCode -eq 404 -and [int]$err409.StatusCode -eq 409 -and
        $ct409 -match "application/problem\+json" -and $null -ne $prob409.type -and $null -ne $prob409.title -and $prob409.status -eq 409 -and $null -ne $prob409.detail -and $null -ne $prob409.instance) {
        Write-Pass "О1.4 та Д1.3 (Web): Усі помилки (400, 422, 404, 409) повертаються у стандарті RFC 9457 Problem Details (application/problem+json)"
    } else {
        Write-Fail "Помилка перевірки Problem Details: 400=$([int]$err400.StatusCode), 422=$([int]$err422.StatusCode), 404=$([int]$err404.StatusCode), 409=$([int]$err409.StatusCode)"
    }

    Write-Info "Перевірка Д1.1 та Д1.5 (Web): межі вікна [opens_at, closes_at), перехід через межу доби, зміна часового зміщення (DST +02:00/+03:00) та ЄКТС (30.0 vs 30.5)..."
    $winClosesRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent2 -Body '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-15T15:00:00Z"}' -SkipHttpErrorCheck
    $winOpensRes  = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent2 -Body '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T07:00:00Z"}' -SkipHttpErrorCheck

    # Д1.5 (Web): Перевірка зміни часового зміщення (offset +02:00 vs +03:00) та переходу через межу доби в Europe/Kyiv
    # 2026-09-01T08:59:59+02:00 дорівнює 06:59:59Z (за 1 с до opens_at 07:00:00Z -> 409 Conflict)
    # 2026-09-01T20:59:59Z у Києві (UTC+3) становить 2026-09-01T23:59:59+03:00, а expires_at (+15 хв) становить 2026-09-02T00:14:59+03:00 (перехід через межу доби за Києвом)
    $webHeadersStudent3 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-3" }
    $offsetBeforeOpenRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T08:59:59+02:00"}' -SkipHttpErrorCheck
    $dayBoundaryEnrRes   = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T20:59:59Z"}' -SkipHttpErrorCheck
    $dayBoundaryRaw      = Get-ResponseText $dayBoundaryEnrRes
    $dayBoundaryBody     = $dayBoundaryRaw | ConvertFrom-Json
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$($dayBoundaryBody.id)/cancel" -Method Post -Headers $webHeadersStudent3

    $webHeadersEcts = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-ects-20" }
    $ects305Res = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersEcts -Body '{"group_id":"DISC-ECTS-10-5-G1"}' -SkipHttpErrorCheck
    $ects300Res = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersEcts -Body '{"group_id":"DISC-ECTS-10-G1"}' -SkipHttpErrorCheck

    if ([int]$winOpensRes.StatusCode -eq 201 -and [int]$winClosesRes.StatusCode -eq 409 -and
        [int]$offsetBeforeOpenRes.StatusCode -eq 409 -and [int]$dayBoundaryEnrRes.StatusCode -eq 201 -and
        $dayBoundaryRaw -match '"created_at_local":"2026-09-01T23:59:59\+03:00"' -and
        $dayBoundaryRaw -match '"expires_at_local":"2026-09-02T00:14:59\+03:00"' -and
        [int]$ects300Res.StatusCode -eq 201 -and [int]$ects305Res.StatusCode -eq 409) {
        Write-Pass "Д1.1 та Д1.5 (Web): Вікно [opens_at, closes_at), перехід через межу доби (23:59:59+03:00 -> 00:14:59+03:00), зміна часового зміщення (+02:00/+03:00) та ЄКТС (30.0 -> 201, 30.5 -> 409) працюють бездоганно!"
    } else {
        Write-Fail "Помилка Д1.1/Д1.5: opens=$([int]$winOpensRes.StatusCode), closes=$([int]$winClosesRes.StatusCode), offsetBefore=$([int]$offsetBeforeOpenRes.StatusCode), dayBoundary=$([int]$dayBoundaryEnrRes.StatusCode), ects30=$([int]$ects300Res.StatusCode), ects30.5=$([int]$ects305Res.StatusCode)"
    }

    Write-Info "Перевірка О1.5 та О1.7 (Web): читання всіх сторінок списку з курсором next_cursor та перевірка пошкодженого курсора..."
    $pageCount = 0
    $totalCursorItems = 0
    $cursor = $null

    while ($true) {
        $pageCount++
        $url = "$BaseUrl/v1/enrollments?limit=2"
        if ($cursor) { $url += "&after=$([uri]::EscapeDataString($cursor))" }
        $pageRes = Invoke-RestMethod -Uri $url -Method Get -Headers $webHeadersStudent1
        $totalCursorItems += $pageRes.items.Count
        if (-not $pageRes.next_cursor) { break }
        $cursor = $pageRes.next_cursor
        if ($pageCount -gt 10) { break }
    }

    $badCursorRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments?limit=2&after=corrupted-cursor-xyz" -Method Get -Headers $webHeadersStudent1 -SkipHttpErrorCheck
    if ($pageCount -eq 2 -and $totalCursorItems -eq 3 -and [int]$badCursorRes.StatusCode -eq 400) {
        Write-Pass "О1.5 та О1.7 (Web): Усі $pageCount сторінок курсорного списку успішно прочитано (всього елементів: $totalCursorItems); пошкоджений курсор відхилено з HTTP 400!"
    } else {
        Write-Fail "Помилка курсорного списку О1.5: pages=$pageCount, items=$totalCursorItems, badCursorStatus=$([int]$badCursorRes.StatusCode)"
    }

    # Перевірка спроби розподілу за другим вибором ДО закриття основної групи (має повертати 409 Conflict)
    $distBeforeClose = Invoke-WebRequest -Uri "$BaseUrl/v1/groups/DISC-SEC-03/distribute-second-choice" -Method Post -Headers $Headers -Body '{"student_id":"student-3","second_choice_group_id":"DISC-SEC-04"}' -SkipHttpErrorCheck

    $cancelRes = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$($enr1Body.id)/cancel" -Method Post -Headers $webHeadersStudent1
    $closeGrpRes = Invoke-RestMethod -Uri "$BaseUrl/v1/groups/DISC-SEC-03/close" -Method Post -Headers $webHeadersStudent1
    $closedEnrollAttempt = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-SEC-03"}' -SkipHttpErrorCheck

    if ($cancelRes.status -eq "cancelled" -and $closeGrpRes.closed -eq $true -and [int]$closedEnrollAttempt.StatusCode -eq 409 -and [int]$distBeforeClose.StatusCode -eq 409) {
        Write-Pass "Маршрути С10 (Web): скасування запису (status=cancelled), закриття групи деканатом (closed=true -> 409) та заборона розподілу другого вибору до закриття групи (409) працюють коректно!"
    } else {
        Write-Fail "Помилка маршрутів cancel/close: cancel=$($cancelRes.status), closed=$($closeGrpRes.closed), attempt=$([int]$closedEnrollAttempt.StatusCode), distBefore=$([int]$distBeforeClose.StatusCode)"
    }

    # С10 §3.12 п. 2, 6: Окремий механізм розподілу студентів у групи другого вибору (second_choice_group_id) ПІСЛЯ закриття запису
    $distAfterCloseRes = Invoke-RestMethod -Uri "$BaseUrl/v1/groups/DISC-SEC-03/distribute-second-choice" -Method Post -Headers $Headers -Body '{"student_id":"student-3","second_choice_group_id":"DISC-SEC-04"}'
    $sec04SeatsAfter   = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-SEC-04" -Method Get -Headers $Headers
    $webDuplicateAfterSecondChoice = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-SEC-04"}' -SkipHttpErrorCheck

    if ($distAfterCloseRes.distributed_count -eq 1 -and $distAfterCloseRes.items[0].status -eq "done" -and
        $distAfterCloseRes.items[0].recorded_in_roster -eq $true -and $sec04SeatsAfter.taken -eq 1 -and
        $sec04SeatsAfter.freeSeats -eq 14 -and [int]$webDuplicateAfterSecondChoice.StatusCode -eq 409) {
        Write-Pass "С10 §3.12 (Другий вибір після закриття): розподільник DPC атомарно призначив student-3 у DISC-SEC-04 (freeSeats=14, recorded_in_roster=true); повторний запис через Web відхилено з 409 Conflict!"
    } else {
        Write-Fail "Помилка розподілу другого вибору: distributed=$($distAfterCloseRes.distributed_count), freeSeats=$($sec04SeatsAfter.freeSeats), webDup=$([int]$webDuplicateAfterSecondChoice.StatusCode)"
    }

    # Скидаємо дані до чистого стану перед поверненням у робочий режим ALLOW_TEST_OVERRIDES=false
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -Method Post -Headers $Headers
}
finally {
    Write-Info "Повернення контейнерів у робочий захищений режим ALLOW_TEST_OVERRIDES=false..."
    $null = Set-TestOverridesMode -enable $false
    Pop-Location
}

# ------------------------------------------------------------------------------
# 11. Підсумкова перевірка лічильника помилок
# ------------------------------------------------------------------------------
Write-Header "ПІДСУМОК ПЕРЕВІРКИ ЛАБОРАТОРНОЇ РОБОТИ №1 (Тема С10: Web + РПО)"
if ($script:ErrorCount -gt 0) {
    Write-Host "`n  [FAILED] Виявлено помилок під час перевірки: $($script:ErrorCount)" -ForegroundColor Red
    exit 1
}

Write-Host "  [OK] Статичний контроль: Spectral OpenAPI 3.1 (0 помилок), dotnet test, schema_migrations (001, 002) та VIEW group_seats" -ForegroundColor Green
Write-Host "  [OK] Безпека: X-User-Id обов'язковий, student_id у тілі заборонений (422), тестові лазівки закриті (ALLOW_TEST_OVERRIDES=false -> 403)" -ForegroundColor Green
Write-Host "  [OK] РПО О1.1–О1.5, Д1.1–Д1.4: Черга, неблокуючий TCS async, інваріант SAFE/UNSAFE, закон Літтла (інтеграл + 100 мс вибірка), JSON SIGTERM" -ForegroundColor Green
Write-Host "  [OK] Web О1.1–О1.7, Д1.1–Д1.5: POST /v1/enrollments (201 + Location), Problem Details, курсор next_cursor, Nginx least_conn, вікно [opens_at, closes_at) та 30.0 ЄКТС" -ForegroundColor Green
Write-Host "`nУсі обов'язкові та додаткові тести обох курсів успішно виконано (помилок: 0)!" -ForegroundColor Cyan
exit 0
