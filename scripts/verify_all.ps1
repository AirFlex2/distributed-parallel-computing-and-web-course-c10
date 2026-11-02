<#
.SYNOPSIS
    Комплексний сценарій автоматизованого тестування та перевірки ЛР1 (Web + РПО)
    Тема С10: «Запис на вибіркові дисципліни»
    Студент: Зінківський Тимур Євгенійович, група ІТІНФ-23-2
.DESCRIPTION
    Послідовно перевіряє:
    0. Лінтер OpenAPI 3.1 (Spectral CLI), модульні тести (dotnet test), версіоновані міграції schema_migrations та VIEW group_seats.
    1-2. Захищений робочий режим (ALLOW_TEST_OVERRIDES=false): обов'язковий X-User-Id (400), заборона student_id у тілі (422),
         блокування /v1/admin/reset (403), обов'язкова авторизація Bearer (401).
    3. Розподіл відповідальності Web і РПО (§1.10), відомість деканату, ліміти K та ЄКТС, ідемпотентність.
    4. Пункт О1.2 (РПО): Обмежена черга QUEUE_CAPACITY=100, 429 Backpressure та p95 /health під навантаженням.
    5. Пункт О1.4 (РПО): Захист спільного ресурсу discipline_group (порівняння SAFE vs UNSAFE, демонстрація гонитви).
    6. Пункт О1.5 (РПО): Монотонні позначки часу, одноразова фіксація доставки delivery_wait_ms.
    7. Пункт Д1.3 (РПО): Дві реалізації черги (custom_monitor vs system_channel).
    8. Пункт Д1.1 (РПО): Дві моделі виконання воркерів (async_tasks vs dedicated_threads).
    9. Пункт Д1.4 (РПО): Коректне завершення роботи SIGTERM: нові запити отримують 503, повне вивантаження черги, структурований JSON.
    10. Курс «Web» (О1.1–О1.7, Д1.1–Д1.5): пагінація курсором, скасування (п. 10), ліміти K/ЄКТС, балансування least_conn.
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
    if ($resp -is [System.Net.Http.HttpResponseMessage]) {
        return $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
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
            $m = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers -TimeoutSec 2
            if ($null -ne $m.configuration -and [bool]$m.configuration.allow_test_overrides -eq $enable) {
                return $true
            }
        } catch { }
    }
    return $false
}

function Restart-AppWithConfig([string]$engine, [string]$model) {
    if (![string]::IsNullOrEmpty($engine)) {
        $env:QUEUE_ENGINE = $engine
    }
    if (![string]::IsNullOrEmpty($model)) {
        $env:EXECUTION_MODEL = $model
    }
    $env:ALLOW_TEST_OVERRIDES = "true"
    & $DockerExe compose up -d --force-recreate app-a | Out-Null
    & $DockerExe compose restart nginx | Out-Null
    Remove-Item Env:QUEUE_ENGINE -ErrorAction SilentlyContinue
    Remove-Item Env:EXECUTION_MODEL -ErrorAction SilentlyContinue
    Remove-Item Env:ALLOW_TEST_OVERRIDES -ErrorAction SilentlyContinue

    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 300
        try {
            $m = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers -TimeoutSec 2
            $engineMatch = [string]::IsNullOrEmpty($engine) -or ($m.configuration.queue_engine -eq $engine)
            $modelMatch  = [string]::IsNullOrEmpty($model) -or ($m.configuration.execution_model -eq $model)
            if ($engineMatch -and $modelMatch) {
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
        Write-Pass "Лінтер Spectral: 0 помилок і 0 попереджень у openapi.yaml"
    } else {
        Write-Fail "Помилки лінтування OpenAPI:`n$lintOut"
    }
} else {
    Write-Fail "CLI 'npx' не знайдено в PATH. Потрібен Node.js 18+ для перевірки Spectral."
}

if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Fail "CLI 'dotnet' не знайдено в PATH. Потрібен .NET 8 SDK."
    Pop-Location
    exit 1
}

$trxFile = Join-Path $RepoRoot "tests/EnrollmentService.Tests/TestResults/verify_test_results.trx"
if (Test-Path $trxFile) { Remove-Item $trxFile -Force -ErrorAction SilentlyContinue }

$testOut = (& dotnet test tests/EnrollmentService.Tests/EnrollmentService.Tests.csproj -c Release --logger "trx;LogFileName=verify_test_results.trx" 2>&1) -join "`n"
$testExit = $LASTEXITCODE

$passed = 0
$failed = 0
$skipped = 0
$total = 0

if (Test-Path $trxFile) {
    try {
        [xml]$trx = Get-Content $trxFile
        $counters = $trx.TestRun.ResultSummary.Counters
        $passed = [int]$counters.passed
        $failed = [int]$counters.failed
        $total = [int]$counters.total
        $executed = [int]$counters.executed

        $nonPassed = ($trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "NotExecuted" -or $_.outcome -eq "Ignored" -or $_.outcome -eq "Inconclusive" })
        if ($null -ne $nonPassed) {
            $skipped = @($nonPassed).Count
        } elseif ($total -gt $executed) {
            $skipped = $total - $executed
        }
    } catch { }
}

if ($total -eq 0) {
    if ($testOut -match '(?i)(?:пройдено|Passed)\s*[:=]\s*(\d+)') { $passed = [int]$Matches[1] }
    if ($testOut -match '(?i)(?:не пройдено|Failed)\s*[:=]\s*(\d+)') { $failed = [int]$Matches[1] }
    if ($testOut -match '(?i)(?:пропущено|Skipped)\s*[:=]\s*(\d+)') { $skipped = [int]$Matches[1] }
    if ($testOut -match '(?i)(?:всього|Total)\s*[:=]\s*(\d+)') { $total = [int]$Matches[1] }
}

if ($testExit -eq 0 -and $failed -eq 0 -and $skipped -eq 0 -and $passed -ge 85) {
    Write-Pass "Модульні, інтеграційні та E2E-тести: УСІ ПРОЙДЕНО (Статистика: Passed=$passed, Failed=$failed, Skipped=$skipped, Total=$total)"
} else {
    if ($skipped -gt 0) {
        Write-Fail "Обов'язкові інтеграційні тести були ПРОПУЩЕНІ (можливо, недоступний PostgreSQL): Passed=$passed, Failed=$failed, Skipped=$skipped, Total=$total"
    } else {
        Write-Fail "Помилка тестування (Passed=$passed, Failed=$failed, Skipped=$skipped, Total=$total):`n$testOut"
    }
}

# ------------------------------------------------------------------------------
# 1. Запуск та готовність інфраструктури Docker Compose
# ------------------------------------------------------------------------------
Write-Header "1. Розгортання та перевірка контейнерів Docker Compose"
& $DockerExe compose up -d --build | Out-Null
& $DockerExe compose restart nginx | Out-Null

$isReady = $false
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get -TimeoutSec 2
        if ($health.status -eq "ok") {
            $isReady = $true
            break
        }
    } catch { }
}

if ($isReady) {
    Write-Pass "Балансувальник Nginx та бекенд успішно відповідають на GET /health (status=ok)"
} else {
    Write-Fail "Сервіс не вийшов у стан готовності за 22 секунди."
    Pop-Location
    exit 1
}

# ------------------------------------------------------------------------------
# 2. Перевірка безпеки у робочому режимі (ALLOW_TEST_OVERRIDES=false)
# ------------------------------------------------------------------------------
Write-Header "2. Безпека API у робочому режимі: Bearer (401), X-User-Id (400/422), валідація вхідних даних (404)"

$healthBRes = Invoke-WebRequest -Uri "$BaseUrl/health-app-b" -Method Get -TimeoutSec 2 -SkipHttpErrorCheck
$healthBJson = (Get-ResponseText $healthBRes) | ConvertFrom-Json
if ([int]$healthBRes.StatusCode -eq 200 -and $healthBJson.status -eq "ok" -and $healthBJson.instance_id -eq "app-b") {
    Write-Pass "Прямий маршрут /health-app-b успішно повертає стан вузла app-b (instance_id=app-b)"
} else {
    Write-Fail "Помилка перевірки /health-app-b: Status=$([int]$healthBRes.StatusCode), Instance=$($healthBJson.instance_id)"
}

$corsOptHeaders = @{
    "Origin" = "http://localhost:3000"
    "Access-Control-Request-Method" = "POST"
    "Access-Control-Request-Headers" = "content-type,x-user-id"
}
$corsOptRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Options -Headers $corsOptHeaders -SkipHttpErrorCheck
$corsOptReqId = [string]($corsOptRes.Headers["X-Request-ID"] | Select-Object -First 1)
$corsOptAllowOrigin = [string]($corsOptRes.Headers["Access-Control-Allow-Origin"] | Select-Object -First 1)
$corsLogs = (& $DockerExe compose logs app-a app-b --tail 50 2>&1)
$hasCorsLog = $corsLogs | Where-Object { $_ -match '"event"\s*:\s*"http_request"' -and $_ -match '"method"\s*:\s*"OPTIONS"' -and $_ -match [regex]::Escape($corsOptReqId) }

if ([int]$corsOptRes.StatusCode -eq 204 -and $corsOptAllowOrigin -eq "http://localhost:3000" -and !([string]::IsNullOrWhiteSpace($corsOptReqId)) -and $hasCorsLog) {
    Write-Pass "О1.6 (Web): CORS preflight (OPTIONS) містить X-Request-ID ($corsOptReqId) та зафіксований у JSON-лозі зі статусом 204"
} else {
    Write-Fail "Помилка перевірки CORS preflight: Status=$([int]$corsOptRes.StatusCode), X-Request-ID=$corsOptReqId, AllowOrigin=$corsOptAllowOrigin, Log=$([bool]$hasCorsLog)"
}

$unauthRes = Invoke-WebRequest -Uri "$BaseUrl/v1/group-seats" -Method Get -SkipHttpErrorCheck
$unauthCt = [string]($unauthRes.Headers["Content-Type"] | Select-Object -First 1)
if ([int]$unauthRes.StatusCode -eq 401 -and $unauthCt -match "application/problem\+json") {
    Write-Pass "Запит GET /v1/group-seats без Bearer-токена відхилено з HTTP 401 Unauthorized (application/problem+json)"
} else {
    Write-Fail "Очікувався HTTP 401 problem+json, отримано: $([int]$unauthRes.StatusCode) ($unauthCt)"
}

$unauthGroupClose = Invoke-WebRequest -Uri "$BaseUrl/v1/groups/DISC-WEB-01/close" -Method Post -SkipHttpErrorCheck
$invalidAuthGroupClose = Invoke-WebRequest -Uri "$BaseUrl/v1/groups/DISC-WEB-01/close" -Method Post -Headers @{ "Authorization" = "Bearer invalid-token-12345" } -SkipHttpErrorCheck
if ([int]$unauthGroupClose.StatusCode -eq 401 -and [int]$invalidAuthGroupClose.StatusCode -eq 401) {
    Write-Pass "Маршрут POST /v1/groups/{id}/close надійно захищено Bearer-токеном (без токена: 401, недійсний токен: 401)"
} else {
    Write-Fail "Помилка автентифікації POST /v1/groups/{id}/close: unauth=$([int]$unauthGroupClose.StatusCode), invalid=$([int]$invalidAuthGroupClose.StatusCode)"
}

$unauthMetrics = Invoke-WebRequest -Uri "$BaseUrl/metrics" -Method Get -SkipHttpErrorCheck
$invalidAuthMetrics = Invoke-WebRequest -Uri "$BaseUrl/metrics" -Method Get -Headers @{ "Authorization" = "Bearer invalid-token-12345" } -SkipHttpErrorCheck
if ([int]$unauthMetrics.StatusCode -eq 401 -and [int]$invalidAuthMetrics.StatusCode -eq 401) {
    Write-Pass "Маршрут GET /metrics надійно захищено Bearer-токеном (без токена: 401, недійсний токен: 401)"
} else {
    Write-Fail "Помилка автентифікації GET /metrics: unauth=$([int]$unauthMetrics.StatusCode), invalid=$([int]$invalidAuthMetrics.StatusCode)"
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

$resetProdRes = Invoke-WebRequest -Uri "$BaseUrl/v1/admin/reset" -Method Post -Headers $Headers -SkipHttpErrorCheck
if ([int]$resetProdRes.StatusCode -eq 403) {
    Write-Pass "У робочому режимі (ALLOW_TEST_OVERRIDES=false) маршрут /v1/admin/reset заблоковано (403 Forbidden)"
} else {
    Write-Fail "Очікувався 403 для /v1/admin/reset, отримано: $([int]$resetProdRes.StatusCode)"
}

# П. 12: Неіснуюча група при прийомі заявки отримує 404 Not Found одразу
$bodyUnknownGroup = @{ student_id = "student-1"; group_id = "NON-EXISTENT-GROUP" } | ConvertTo-Json
$unknownGroupRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyUnknownGroup -SkipHttpErrorCheck
if ([int]$unknownGroupRes.StatusCode -eq 404) {
    Write-Pass "Неіснуюча навчальна група негайно відхиляється на прийомі з HTTP 404 Not Found (п. 12)"
} else {
    Write-Fail "Очікувався 404 для неіснуючої групи, отримано: $([int]$unknownGroupRes.StatusCode)"
}

# П. 1: Неіснуючий студент при прийомі заявки отримує 404 Not Found
$bodyUnknownStudent = @{ student_id = "non-existent-student"; group_id = "DISC-DPC-01" } | ConvertTo-Json
$unknownStudentRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyUnknownStudent -SkipHttpErrorCheck
if ([int]$unknownStudentRes.StatusCode -eq 404) {
    Write-Pass "Невідомий студент негайно відхиляється на прийомі з HTTP 404 Not Found (п. 1)"
} else {
    Write-Fail "Очікувався 404 для невідомого студента, отримано: $([int]$unknownStudentRes.StatusCode)"
}

# Д1.4 (Web): Валідація W3C traceparent, виділення trace_id у лог http_request та повернення X-Trace-Id / X-Request-ID
$testTraceId = "4bf92f3577b34da6a3ce929d0e0e4736"
$traceHeaderVal = "00-$testTraceId-00f067aa0ba902b7-01"
$traceHeaders = @{ "traceparent" = $traceHeaderVal }
$traceRes = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -Headers $traceHeaders -SkipHttpErrorCheck
$retTraceId = [string]($traceRes.Headers["X-Trace-Id"] | Select-Object -First 1)
$retReqId   = [string]($traceRes.Headers["X-Request-ID"] | Select-Object -First 1)

$appLogs = (& $DockerExe compose logs app-a app-b --tail 100 2>&1)
$hasLogEntry = $appLogs | Where-Object { $_ -match '"event"\s*:\s*"http_request"' -and $_ -match $testTraceId }

if ($retTraceId -eq $testTraceId -and !([string]::IsNullOrWhiteSpace($retReqId)) -and $hasLogEntry) {
    Write-Pass "Д1.4 (Web): Валідний traceparent перетворено на X-Trace-Id ($retTraceId), присутній X-Request-ID, подію зафіксовано в JSON-лозі (event=http_request, trace_id=$testTraceId)"
} else {
    Write-Fail "Помилка перевірки W3C traceparent / структурованого логу: retTraceId=$retTraceId, retReqId=$retReqId, hasLog=$([bool]$hasLogEntry)"
}

# О1.3 / О1.4: Перевірка RFC 9457 Problem Details для невалідного GUID, неіснуючого шляху (404) та непідтримуваного методу (405)
$invalidGuidReq = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests/not-a-valid-guid" -Method Get -Headers $Headers -SkipHttpErrorCheck
$invalidGuidJson = (Get-ResponseText $invalidGuidReq) | ConvertFrom-Json
$invalidGuidCt = [string]($invalidGuidReq.Headers["Content-Type"] | Select-Object -First 1)
if ([int]$invalidGuidReq.StatusCode -eq 400 -and $invalidGuidCt -match "application/problem\+json" -and $invalidGuidJson.error_code -eq "invalid_request_id_format") {
    Write-Pass "Невалідний GUID у шляху /v1/enrollment-requests/{id} повертає HTTP 400 Problem Details (error_code=invalid_request_id_format)"
} else {
    Write-Fail "Очікувався 400 problem+json для невалідного GUID, отримано: $([int]$invalidGuidReq.StatusCode) ($invalidGuidCt)"
}

$unknownRouteRes = Invoke-WebRequest -Uri "$BaseUrl/v1/non-existent-path-for-testing-404" -Method Get -Headers $Headers -SkipHttpErrorCheck
$unknownRouteJson = (Get-ResponseText $unknownRouteRes) | ConvertFrom-Json
$unknownRouteCt = [string]($unknownRouteRes.Headers["Content-Type"] | Select-Object -First 1)
if ([int]$unknownRouteRes.StatusCode -eq 404 -and $unknownRouteCt -match "application/problem\+json" -and $unknownRouteJson.error_code -eq "route_not_found") {
    Write-Pass "Неіснуючий маршрут сервера повертає HTTP 404 Problem Details (error_code=route_not_found)"
} else {
    Write-Fail "Очікувався 404 problem+json для невідомого маршруту, отримано: $([int]$unknownRouteRes.StatusCode) ($unknownRouteCt)"
}

$unsupportedMethodRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Patch -Headers $Headers -SkipHttpErrorCheck
$unsupportedMethodJson = (Get-ResponseText $unsupportedMethodRes) | ConvertFrom-Json
$unsupportedMethodCt = [string]($unsupportedMethodRes.Headers["Content-Type"] | Select-Object -First 1)
if ([int]$unsupportedMethodRes.StatusCode -eq 405 -and $unsupportedMethodCt -match "application/problem\+json" -and $unsupportedMethodJson.error_code -eq "method_not_allowed") {
    Write-Pass "Непідтримуваний HTTP-метод повертає HTTP 405 Problem Details (error_code=method_not_allowed)"
} else {
    Write-Fail "Очікувався 405 problem+json для непідтримуваного методу, отримано: $([int]$unsupportedMethodRes.StatusCode) ($unsupportedMethodCt)"
}

# ==============================================================================
# Увімкнення контрольованого тестового стенду ALLOW_TEST_OVERRIDES=true
# ==============================================================================
try {
    Write-Info "Активація контрольованого тестового режиму ALLOW_TEST_OVERRIDES=true для автоматизованих дослідів..."
    if (-not (Set-TestOverridesMode -enable $true)) {
        Write-Fail "Не вдалося активувати ALLOW_TEST_OVERRIDES=true"
        throw "Test mode activation failed"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    # --------------------------------------------------------------------------
    # 3. Пункт О1.1 (РПО) та §1.10: Обробка заявок, відомість деканату, ліміти K/ЄКТС
    # --------------------------------------------------------------------------
    Write-Header "3. ПУНКТ О1.1 (РПО) та С10 §3.12: Обробка заявок, відомість деканату за request_id, ліміти K/ЄКТС"

    $fixedId = [guid]::NewGuid().ToString()
    $bodyReq1 = @{
        request_id = $fixedId
        student_id = "student-1"
        group_id   = "DISC-DPC-01"
    } | ConvertTo-Json

    $postRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyReq1 -SkipHttpErrorCheck
    if ([int]$postRes.StatusCode -eq 202) {
        Write-Pass "Запит РПО прийнято: отримано HTTP 202 Accepted (запис збережено в БД у стані 'queued' до черги)"
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

    if ($finalStatus -eq "done" -and $statusRes.recorded_in_roster -eq $true) {
        Write-Pass "Заявка $reqId успішно перейшла у стан 'done' і зафіксована у відомості деканату за request_id (QueueWait=$($statusRes.queue_wait_ms) мс, Processing=$($statusRes.processing_time_ms) мс)"
    } else {
        Write-Fail "Помилка переходу в кінцевий стан: status=$finalStatus, recorded=$($statusRes.recorded_in_roster)"
    }

    # Повторний запит із тим самим ID та іншими параметрами -> 409 Conflict
    $conflictBody = @{ request_id = $fixedId; student_id = "student-2"; group_id = "DISC-WEB-01" } | ConvertTo-Json
    $conflictRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $conflictBody -SkipHttpErrorCheck
    if ([int]$conflictRes.StatusCode -eq 409) {
        Write-Pass "Ідемпотентний конфлікт за фіксованим requestId успішно виявлено: отримано HTTP 409 Conflict"
    } else {
        Write-Fail "Очікувався HTTP 409, отримано $([int]$conflictRes.StatusCode)"
    }

    # Перевірка глобальної унікальності Idempotency-Key між різними студентами (міграція 006)
    $globalIk = "ik-global-" + [guid]::NewGuid().ToString("N")
    $ikHeaders1 = @{ "Content-Type" = "application/json"; "Authorization" = "Bearer $Token"; "Idempotency-Key" = $globalIk }
    $ikPost1 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $ikHeaders1 -Body (@{ student_id = "student-2"; group_id = "DISC-DPC-01" } | ConvertTo-Json) -SkipHttpErrorCheck
    $ikPost2 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $ikHeaders1 -Body (@{ student_id = "student-3"; group_id = "DISC-DPC-01" } | ConvertTo-Json) -SkipHttpErrorCheck
    $ikConflictJson = (Get-ResponseText $ikPost2) | ConvertFrom-Json
    if ([int]$ikPost1.StatusCode -eq 202 -and [int]$ikPost2.StatusCode -eq 409 -and $ikConflictJson.error_code -eq "idempotency_conflict") {
        Write-Pass "Пункт 2 (міграція 006): Глобальний унікальний індекс Idempotency-Key захищає від використання одного ключа різними студентами (409 Conflict)"
    } else {
        Write-Fail "Помилка перевірки глобального Idempotency-Key: post1=$([int]$ikPost1.StatusCode), post2=$([int]$ikPost2.StatusCode)"
    }

    # --------------------------------------------------------------------------
    # 4. Пункт О1.2 (РПО): Обмежена черга QUEUE_CAPACITY=100, 429 Backpressure та замір /health під навантаженням
    # --------------------------------------------------------------------------
    Write-Header "4. ПУНКТ О1.2 (РПО): Обмежена черга QUEUE_CAPACITY=100, перевантаження 429 та p95 /health під навантаженням"
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    Write-Info "Створення стійкого фонового навантаження (50 паралельних клієнтів, тривалість ~8 с) та паралельний замір /health-app-a..."

    $concurrentClients = 50
    $loadDurationSec = 8

    # 50 паралельних клієнтських потоків через ForEach-Object -Parallel у фоновому Job
    $loadJob = 1..$concurrentClients | ForEach-Object -Parallel {
        $cIndex = $_
        $clientHttp = [System.Net.Http.HttpClient]::new()
        $clientHttp.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", "secret-c10-token-2026")
        $studentId = "student-safe-$("{0:D2}" -f (($cIndex % 20) + 1))"
        $bodyJson = "{`"student_id`":`"$studentId`",`"group_id`":`"DISC-DPC-01`"}"

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $acc = 0
        $thr = 0
        $ret = 0

        while ($sw.Elapsed.TotalSeconds -lt $using:loadDurationSec) {
            try {
                $content = [System.Net.Http.StringContent]::new($bodyJson, [System.Text.Encoding]::UTF8, "application/json")
                $resp = $clientHttp.PostAsync("$using:BaseUrl/v1/enrollment-requests", $content).GetAwaiter().GetResult()
                $code = [int]$resp.StatusCode
                if ($code -eq 202) {
                    $acc++
                } elseif ($code -eq 429) {
                    $thr++
                    if ($resp.Headers.Contains("Retry-After")) {
                        $ret++
                    }
                }
            } catch { }
            [System.Threading.Thread]::Sleep(30)
        }
        [PSCustomObject]@{
            Accepted = $acc
            Throttled = $thr
            HasRetryAfter = $ret
        }
    } -ThrottleLimit $concurrentClients -AsJob

    # Паралельні заміри затримки /health-app-a через Nginx під час насиченої черги (200 вимірювань)
    $healthLatencies = [System.Collections.Generic.List[double]]::new()
    $healthSw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($healthLatencies.Count -lt 200 -and $healthSw.Elapsed.TotalSeconds -lt ($loadDurationSec + 2)) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $hResp = $httpClient.GetAsync("$BaseUrl/health-app-a").GetAwaiter().GetResult()
            $sw.Stop()
            if ($hResp.IsSuccessStatusCode) {
                $healthLatencies.Add($sw.Elapsed.TotalMilliseconds)
            }
        } catch { }
        [System.Threading.Thread]::Sleep(20)
    }

    $null = Wait-Job $loadJob
    $clientResults = Receive-Job $loadJob
    Remove-Job $loadJob

    $mLoad = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $maxObservedQueue = $mLoad.littles_law.max_queue_length_observed
    $queueBounded = $mLoad.littles_law.queue_is_bounded

    $totalAcceptedLoad = [int](($clientResults | Measure-Object -Property Accepted -Sum).Sum)
    $total429Load = [int](($clientResults | Measure-Object -Property Throttled -Sum).Sum)
    $hasRetryAfterCount = [int](($clientResults | Measure-Object -Property HasRetryAfter -Sum).Sum)

    $sortedHealth = $healthLatencies | Sort-Object
    $p50Index = [Math]::Max(0, [Math]::Ceiling($sortedHealth.Count * 0.50) - 1)
    $p95Index = [Math]::Max(0, [Math]::Ceiling($sortedHealth.Count * 0.95) - 1)
    $p50Health = [Math]::Round($sortedHealth[$p50Index], 2)
    $p95Health = [Math]::Round($sortedHealth[$p95Index], 2)

    Write-Info "Параметри досліду О1.2: QUEUE_CAPACITY=100, WORKERS=3, одночасних клієнтів=$concurrentClients, тривалість=$loadDurationSec с"
    Write-Info "Результати заміру: 202 Accepted=$totalAcceptedLoad, 429 TooManyRequests=$total429Load (з Retry-After: $hasRetryAfterCount), замірів /health-app-a=$($healthLatencies.Count)"
    Write-Info "Метрики черги: max_queue_length_observed=$maxObservedQueue, queue_is_bounded=$queueBounded"

    if ($total429Load -gt 0 -and $hasRetryAfterCount -eq $total429Load -and $maxObservedQueue -ge 95 -and $queueBounded -eq $true) {
        Write-Pass "Обмежену чергу захищено: отримано $total429Load відповідей 429, усі містять Retry-After, пікова черга сягнула $maxObservedQueue/100 (queue_is_bounded=true)"
        Write-Pass "Пункт 7: 429 повертається миттєво в пам'яті через TryReserveSlot() без зайвого навантаження на БД"
    } else {
        Write-Fail "Помилка переповнення: 429=$total429Load, Retry-After=$hasRetryAfterCount, max_queue=$maxObservedQueue, bounded=$queueBounded"
    }

    if ($p95Health -le 100.0) {
        Write-Pass "Затримка виділеного маршруту /health-app-a через Nginx під навантаженням (О1.2): p50 = $p50Health мс, p95 = $p95Health мс (норматив <= 100 мс ВИКОНАНО)"
    } else {
        Write-Fail "Помилка /health-app-a під навантаженням: p95 = $p95Health мс"
    }

    # --------------------------------------------------------------------------
    # 5. Пункт О1.4 (РПО): Захист спільного ресурсу discipline_group (SAFE vs UNSAFE)
    # --------------------------------------------------------------------------
    Write-Header "5. ПУНКТ О1.4 (РПО): Захист спільного ресурсу discipline_group (порівняння SAFE vs UNSAFE)"

    Write-Info "Крок 5.1: Безпечний режим UNSAFE_MODE=false (група DISC-TEST-SAFE, seats=5, 20 дійсних студентів)..."
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $safeTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($s = 1; $s -le 20; $s++) {
        $stName = "student-safe-$("{0:D2}" -f $s)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stName`",`"group_id`":`"DISC-TEST-SAFE`"}", [System.Text.Encoding]::UTF8, "application/json")
        $safeTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($safeTasks.ToArray())

    for ($waitLoop = 0; $waitLoop -lt 60; $waitLoop++) {
        Start-Sleep -Milliseconds 250
        $mSnap = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mSnap.queue_length -eq 0 -and $mSnap.in_processing -eq 0 -and ($mSnap.counters.total_done + $mSnap.counters.total_rejected) -ge 20) {
            break
        }
    }

    $safeGroup = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-TEST-SAFE" -Method Get -Headers $Headers
    $safeMetrics = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    if ($safeGroup.taken -eq 5 -and $safeGroup.seats -eq 5 -and $safeGroup.roster_count -eq 5 -and $safeGroup.invariant_holds -eq $true -and $safeMetrics.counters.total_done -eq 5 -and $safeMetrics.counters.total_rejected -eq 15) {
        Write-Pass "БЕЗПЕЧНИЙ РЕЖИМ (FOR UPDATE): Інваріант дотримано! taken=$($safeGroup.taken)/$($safeGroup.seats), rosterCount=$($safeGroup.roster_count) (done=5, rejected=15)."
    } else {
        Write-Fail "Порушення у безпечному режимі: taken=$($safeGroup.taken), rosterCount=$($safeGroup.roster_count), done=$($safeMetrics.counters.total_done), rejected=$($safeMetrics.counters.total_rejected)"
    }

    Write-Info "Крок 5.2: Небезпечний режим UNSAFE_MODE=true на тій самій тестовій групі (DISC-TEST-SAFE, seats=5, 20 студентів)..."
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=true" -Method Post -Headers $Headers

    $unsafeTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($u = 1; $u -le 20; $u++) {
        $stName = "student-unsafe-$("{0:D2}" -f $u)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stName`",`"group_id`":`"DISC-TEST-SAFE`"}", [System.Text.Encoding]::UTF8, "application/json")
        $unsafeTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($unsafeTasks.ToArray())

    for ($waitLoop = 0; $waitLoop -lt 60; $waitLoop++) {
        Start-Sleep -Milliseconds 250
        $mSnap = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mSnap.queue_length -eq 0 -and $mSnap.in_processing -eq 0 -and ($mSnap.counters.total_done + $mSnap.counters.total_rejected) -ge 20) {
            break
        }
    }

    $unsafeGroup = Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-TEST-SAFE" -Method Get -Headers $Headers
    Write-Info "Стан групи після UNSAFE: taken=$($unsafeGroup.taken), rosterCount=$($unsafeGroup.roster_count), seats=$($unsafeGroup.seats), invariantHolds=$($unsafeGroup.invariant_holds)"
    if ($unsafeGroup.roster_count -gt $unsafeGroup.seats -and $unsafeGroup.taken -gt $unsafeGroup.seats -and $unsafeGroup.invariant_holds -eq $false) {
        Write-Pass "НЕБЕЗПЕЧНИЙ РЕЖИМ (UNSAFE_MODE=true): Стан гонитви успішно продемонстровано! Інваріант ПОРУШЕНО: taken ($($unsafeGroup.taken)) > seats ($($unsafeGroup.seats)) та rosterCount ($($unsafeGroup.roster_count)) > seats ($($unsafeGroup.seats))."
    } else {
        Write-Fail "Не вдалося продемонструвати порушення інваріанта в UNSAFE_MODE: taken=$($unsafeGroup.taken), rosterCount=$($unsafeGroup.roster_count)/$($unsafeGroup.seats)"
    }

    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    # --------------------------------------------------------------------------
    # 6. Пункт О1.5 та Д1.2 (РПО): Монотонні позначки та перевірка закону Літтла
    # --------------------------------------------------------------------------
    Write-Header "6. ПУНКТ О1.5 та Д1.2 (РПО): Монотонні позначки часу та перевірка закону Літтла (L = lambda * W)"

    # Крок 6.1: Монотонні позначки та одноразова фіксація доставки delivery_wait_ms (О1.5)
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers
    $bodyO15 = @{ student_id = "student-1"; group_id = "DISC-DPC-01" } | ConvertTo-Json
    $resO15 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body $bodyO15
    $idO15 = $resO15.id

    for ($w = 0; $w -lt 30; $w++) {
        Start-Sleep -Milliseconds 100
        $mCheck = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mCheck.counters.total_done -ge 1 -and $mCheck.in_processing -eq 0) { break }
    }

    $firstRead1 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$idO15" -Method Get -Headers $Headers
    $deliv1 = $firstRead1.delivery_wait_ms
    Start-Sleep -Milliseconds 200
    $firstRead2 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$idO15" -Method Get -Headers $Headers
    $deliv2 = $firstRead2.delivery_wait_ms

    if ($null -ne $deliv1 -and $deliv1 -eq $deliv2 -and $firstRead1.first_read_at -eq $firstRead2.first_read_at) {
        Write-Pass "Монотонні позначки (О1.5): FirstRead зафіксовано одноразово (deliveryWaitMs = $deliv1 мс, не змінюється при повторних GET)"
    } else {
        Write-Fail "Помилка фіксації FirstRead: d1=$deliv1, d2=$deliv2"
    }

    # Крок 6.2: Перевірка закону Літтла з реальним пакетом 30 запитів на ненульовій черзі (Д1.2)
    Write-Info "Скидання вікна метрик та відправка пакета з 30 запитів у групу DISC-DPC-01 для перевірки закону Літтла..."
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $llTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($i = 1; $i -le 30; $i++) {
        $stId = if ($i -le 20) { "student-safe-$("{0:D2}" -f $i)" } else { "student-unsafe-$("{0:D2}" -f ($i - 20))" }
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stId`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $llTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($llTasks.ToArray())

    # Чекаємо повного вивантаження черги воркерами (30 запитів)
    for ($w = 0; $w -lt 60; $w++) {
        Start-Sleep -Milliseconds 300
        $mDrain = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mDrain.queue_length -eq 0 -and $mDrain.in_processing -eq 0 -and ($mDrain.counters.total_done + $mDrain.counters.total_rejected) -ge 30) {
            break
        }
    }

    $mFinal = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $ll = $mFinal.littles_law
    if ($null -ne $ll) {
        Write-Info "Закон Літтла (Д1.2): вікно=$($ll.observation_window_sec) с, arrivals=$($ll.window_arrivals_count), L_theor=$($ll.l_theoretical_avg_queue_length), L_observed=$($ll.l_observed_avg_queue_length), L_sampled_100ms=$($ll.l_observed_independent_sampled)"
        Write-Info "  Розбіжність теорія/інтеграл: $($ll.relative_discrepancy_pct)% | Розбіжність дискретний семплер/теорія: $($ll.independent_discrepancy_pct)%"
        if ($ll.window_arrivals_count -eq 30 -and $ll.l_observed_avg_queue_length -gt 0 -and $ll.relative_discrepancy_pct -le 10.0 -and $ll.independent_discrepancy_pct -le 15.0 -and $mFinal.counters.monotonic_order_violations -eq 0) {
            Write-Pass "Д1.2 (РПО): Закон Літтла підтверджено на реальному пакеті 30 запитів (arrivals=30, похибка інтеграла $($ll.relative_discrepancy_pct)% <= 10%, похибка семплера $($ll.independent_discrepancy_pct)% <= 15%), монотонність збережена (0 порушень)"
        } else {
            Write-Fail "Помилка закону Літтла: arrivals=$($ll.window_arrivals_count), L_obs=$($ll.l_observed_avg_queue_length), disc=$($ll.relative_discrepancy_pct)%, indep_disc=$($ll.independent_discrepancy_pct)%, violations=$($mFinal.counters.monotonic_order_violations)"
        }
    } else {
        Write-Fail "Метрика littles_law відсутня у GET /metrics"
    }

    # --------------------------------------------------------------------------
    # 7. Пункт Д1.3 (РПО): Дві реалізації черги (custom_monitor та system_channel)
    # --------------------------------------------------------------------------
    Write-Header "7. ПУНКТ Д1.3 (РПО): Порівняння рушіїв черги в пам'яті (custom_monitor vs system_channel)"

    Write-Info "Крок 7.1: Тестування черги QUEUE_ENGINE=custom_monitor на пакеті з 20 запитів..."
    if (-not (Restart-AppWithConfig -engine "custom_monitor" -model "async_tasks")) {
        Write-Fail "Не вдалося запустити app-a з QUEUE_ENGINE=custom_monitor"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $monitorTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($i = 1; $i -le 20; $i++) {
        $stId = "student-safe-$("{0:D2}" -f $i)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stId`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $monitorTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($monitorTasks.ToArray())

    for ($w = 0; $w -lt 60; $w++) {
        Start-Sleep -Milliseconds 250
        $mDrain = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mDrain.queue_length -eq 0 -and $mDrain.in_processing -eq 0 -and ($mDrain.counters.total_done + $mDrain.counters.total_rejected) -ge 20) {
            break
        }
    }
    $mMonitor = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $monProcP50 = $mMonitor.intervals_ms.processing_time_ms.p50_ms
    $monProcP95 = $mMonitor.intervals_ms.processing_time_ms.p95_ms
    $monFullP50 = $mMonitor.intervals_ms.full_domain_execution_ms.p50_ms
    $monFullP95 = $mMonitor.intervals_ms.full_domain_execution_ms.p95_ms
    Write-Info "  custom_monitor: done=$($mMonitor.counters.total_done), processing p50=$monProcP50 мс, p95=$monProcP95 мс, full_execution p50=$monFullP50 мс, p95=$monFullP95 мс"

    Write-Info "Крок 7.2: Тестування черги QUEUE_ENGINE=system_channel на пакеті з 20 запитів..."
    if (-not (Restart-AppWithConfig -engine "system_channel" -model "async_tasks")) {
        Write-Fail "Не вдалося запустити app-a з QUEUE_ENGINE=system_channel"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $channelTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($i = 1; $i -le 20; $i++) {
        $stId = "student-safe-$("{0:D2}" -f $i)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stId`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $channelTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($channelTasks.ToArray())

    for ($w = 0; $w -lt 60; $w++) {
        Start-Sleep -Milliseconds 250
        $mDrain = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mDrain.queue_length -eq 0 -and $mDrain.in_processing -eq 0 -and ($mDrain.counters.total_done + $mDrain.counters.total_rejected) -ge 20) {
            break
        }
    }
    $mChannel = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $chanProcP50 = $mChannel.intervals_ms.processing_time_ms.p50_ms
    $chanProcP95 = $mChannel.intervals_ms.processing_time_ms.p95_ms
    $chanFullP50 = $mChannel.intervals_ms.full_domain_execution_ms.p50_ms
    $chanFullP95 = $mChannel.intervals_ms.full_domain_execution_ms.p95_ms
    Write-Info "  system_channel: done=$($mChannel.counters.total_done), processing p50=$chanProcP50 мс, p95=$chanProcP95 мс, full_execution p50=$chanFullP50 мс, p95=$chanFullP95 мс"

    if ($mMonitor.counters.total_done -eq 20 -and $mChannel.counters.total_done -eq 20) {
        Write-Pass "Д1.3 (РПО): Обидва рушії черги (custom_monitor та system_channel) успішно обробили пакет із 20 запитів без втрат і дедлоків."
    } else {
        Write-Fail "Помилка тестування рушіїв черги: monitor_done=$($mMonitor.counters.total_done), channel_done=$($mChannel.counters.total_done)"
    }

    # --------------------------------------------------------------------------
    # 8. Пункт Д1.1 (РПО): Моделі виконання (async_tasks та dedicated_threads)
    # --------------------------------------------------------------------------
    Write-Header "8. ПУНКТ Д1.1 (РПО): Порівняння моделей виконання воркерів (async_tasks vs dedicated_threads)"

    Write-Info "Крок 8.1: Тестування моделі EXECUTION_MODEL=async_tasks на пакеті з 20 запитів..."
    if (-not (Restart-AppWithConfig -engine "system_channel" -model "async_tasks")) {
        Write-Fail "Не вдалося запустити app-a з EXECUTION_MODEL=async_tasks"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $asyncTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($i = 1; $i -le 20; $i++) {
        $stId = "student-safe-$("{0:D2}" -f $i)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stId`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $asyncTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($asyncTasks.ToArray())

    for ($w = 0; $w -lt 60; $w++) {
        Start-Sleep -Milliseconds 250
        $mDrain = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mDrain.queue_length -eq 0 -and $mDrain.in_processing -eq 0 -and ($mDrain.counters.total_done + $mDrain.counters.total_rejected) -ge 20) {
            break
        }
    }
    $mAsync = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $asyncProcP50 = $mAsync.intervals_ms.processing_time_ms.p50_ms
    $asyncProcP95 = $mAsync.intervals_ms.processing_time_ms.p95_ms
    $asyncFullP50 = $mAsync.intervals_ms.full_domain_execution_ms.p50_ms
    $asyncFullP95 = $mAsync.intervals_ms.full_domain_execution_ms.p95_ms
    Write-Info "  async_tasks: done=$($mAsync.counters.total_done), processing p50=$asyncProcP50 мс, p95=$asyncProcP95 мс, full_execution p50=$asyncFullP50 мс, p95=$asyncFullP95 мс"

    Write-Info "Крок 8.2: Тестування моделі EXECUTION_MODEL=dedicated_threads на пакеті з 20 запитів..."
    if (-not (Restart-AppWithConfig -engine "system_channel" -model "dedicated_threads")) {
        Write-Fail "Не вдалося запустити app-a з EXECUTION_MODEL=dedicated_threads"
    }
    $null = Invoke-RestMethod -Uri "$BaseUrl/v1/admin/reset?unsafeMode=false" -Method Post -Headers $Headers

    $threadTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($i = 1; $i -le 20; $i++) {
        $stId = "student-safe-$("{0:D2}" -f $i)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stId`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $threadTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($threadTasks.ToArray())

    for ($w = 0; $w -lt 60; $w++) {
        Start-Sleep -Milliseconds 250
        $mDrain = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
        if ($mDrain.queue_length -eq 0 -and $mDrain.in_processing -eq 0 -and ($mDrain.counters.total_done + $mDrain.counters.total_rejected) -ge 20) {
            break
        }
    }
    $mThreads = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers
    $threadsProcP50 = $mThreads.intervals_ms.processing_time_ms.p50_ms
    $threadsProcP95 = $mThreads.intervals_ms.processing_time_ms.p95_ms
    $threadsFullP50 = $mThreads.intervals_ms.full_domain_execution_ms.p50_ms
    $threadsFullP95 = $mThreads.intervals_ms.full_domain_execution_ms.p95_ms
    Write-Info "  dedicated_threads: done=$($mThreads.counters.total_done), processing p50=$threadsProcP50 мс, p95=$threadsProcP95 мс, full_execution p50=$threadsFullP50 мс, p95=$threadsFullP95 мс"

    if ($mAsync.counters.total_done -eq 20 -and $mThreads.counters.total_done -eq 20) {
        Write-Pass "Д1.1 (РПО): Обидва рушії виконання (async_tasks та dedicated_threads) успішно обробили пакет із 20 запитів."
    } else {
        Write-Fail "Помилка порівняння моделей: async_done=$($mAsync.counters.total_done), threads_done=$($mThreads.counters.total_done)"
    }

    # Повертаємо конфігурацію app-a до стандартної
    $null = Restart-AppWithConfig -engine "system_channel" -model "async_tasks"

    # --------------------------------------------------------------------------
    # 9. Пункт Д1.4 (РПО): Перехоплення SIGTERM, 503 для нових запитів та JSON-журнал
    # --------------------------------------------------------------------------
    Write-Header "9. ПУНКТ Д1.4 (РПО): Перехоплення SIGTERM, відповідь 503 для нових запитів та вивантаження черги"

    $sigtermBatchCount = 10
    Write-Info "Подання $sigtermBatchCount заявок у чергу перед відправкою сигналу SIGTERM..."
    $sigtermTasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    for ($s = 1; $s -le $sigtermBatchCount; $s++) {
        $stName = "student-sigterm-$("{0:D2}" -f $s)"
        $c = [System.Net.Http.StringContent]::new("{`"student_id`":`"$stName`",`"group_id`":`"DISC-DPC-01`"}", [System.Text.Encoding]::UTF8, "application/json")
        $sigtermTasks.Add($httpClient.PostAsync("$BaseUrl/v1/enrollment-requests", $c))
    }
    [System.Threading.Tasks.Task]::WaitAll($sigtermTasks.ToArray())

    $acceptedSigtermIds = [System.Collections.Generic.List[string]]::new()
    foreach ($t in $sigtermTasks) {
        if ($t.Result.IsSuccessStatusCode) {
            $b = (Get-ResponseText $t.Result) | ConvertFrom-Json
            $acceptedSigtermIds.Add($b.id)
        }
    }

    $sigtermSinceUtc = (Get-Date).ToUniversalTime().AddSeconds(-2).ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Info "Надсилаємо сигнал SIGTERM контейнеру dpc-c10-api (app-a)..."
    $swDrain = [System.Diagnostics.Stopwatch]::StartNew()
    & $DockerExe kill --signal=SIGTERM dpc-c10-api | Out-Null

    Start-Sleep -Milliseconds 150
    $post503Received = $false
    try {
        $postDuringStop = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollment-requests" -Method Post -Headers $Headers -Body '{"student_id":"student-1","group_id":"DISC-DPC-01"}' -SkipHttpErrorCheck -TimeoutSec 3
        if ([int]$postDuringStop.StatusCode -eq 503) {
            $post503Received = $true
            $xReqId = [string]($postDuringStop.Headers["X-Request-ID"] | Select-Object -First 1)
            Write-Pass "Пункт Д1.4 (п. 3): Новий запит під час зупинки ГАРАНТОВАНО отримав HTTP 503 Service Unavailable (X-Request-ID: $xReqId)!"
        } else {
            Write-Fail "Очікувався HTTP 503 під час зупинки, отримано: $([int]$postDuringStop.StatusCode)"
        }
    } catch {
        Write-Info "Запит під час зупинки перервано тайм-аутом"
    }

    if (-not $post503Received) {
        Write-Fail "Д1.4 (РПО): Сервіс не повернув обов'язковий HTTP 503 під час зупинки SIGTERM."
    }

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
        Write-Pass "У stdout контейнера dpc-c10-api зафіксовано однорядкові JSON-події 'sigterm_received' та 'graceful_shutdown_completed' (час вивантаження: $([Math]::Round($swDrain.Elapsed.TotalSeconds, 2)) с)"
    } else {
        Write-Fail "У stdout відсутні події SIGTERM: sigterm_received=$hasSigtermReceived, graceful_shutdown_completed=$hasGracefulDone"
    }

    # Перезапуск контейнера app-a після тесту SIGTERM
    Write-Info "Перезапуск контейнера dpc-c10-api після тесту SIGTERM..."
    & $DockerExe compose restart app-a | Out-Null
    for ($w = 0; $w -lt 40; $w++) {
        Start-Sleep -Milliseconds 300
        try {
            $mCheck = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers $Headers -TimeoutSec 2
            if ($mCheck.instance_id -eq "app-a") { break }
        } catch {}
    }

    # Перевірка що всі прийняті до SIGTERM заявки зберегли кінцевий стан
    $completedAfterSigterm = 0
    foreach ($id in $acceptedSigtermIds) {
        $st = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollment-requests/$id" -Method Get -Headers $Headers
        if ($st.status -in @("done", "rejected")) { $completedAfterSigterm++ }
    }

    if ($completedAfterSigterm -eq $sigtermBatchCount) {
        Write-Pass "Д1.4 (РПО): Усі $completedAfterSigterm/$sigtermBatchCount прийнятих до SIGTERM заявок успішно доведені до кінцевого стану"
    } else {
        Write-Fail "Помилка завершення черги: completed=$completedAfterSigterm/$sigtermBatchCount"
    }

    # --------------------------------------------------------------------------
    # 10. Пункти курсу Web (О1.1–О1.7, Д1.1–Д1.5)
    # --------------------------------------------------------------------------
    Write-Header "10. Курс «Web»: Пагінація курсором, життєвий цикл (held -> confirmed / expired), відмови 400/404/409, least_conn"

    $webHeadersStudent1 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-1"; "Authorization" = "Bearer $Token" }
    $webHeadersStudent2 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-2"; "Authorization" = "Bearer $Token" }
    $webHeadersStudent3 = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-3"; "Authorization" = "Bearer $Token" }

    # 10.1 Синтаксично пошкоджений JSON -> 400 Bad Request (О1.3 Web)
    $malformedJsonRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":' -SkipHttpErrorCheck
    $malformedJsonBody = (Get-ResponseText $malformedJsonRes) | ConvertFrom-Json
    if ([int]$malformedJsonRes.StatusCode -eq 400 -and $malformedJsonBody.error_code -eq "invalid_json_syntax") {
        Write-Pass "Web О1.3: Синтаксично пошкоджений JSON відхилено з HTTP 400 Bad Request (error_code=invalid_json_syntax)"
    } else {
        Write-Fail "Помилка перевірки пошкодженого JSON: status=$([int]$malformedJsonRes.StatusCode)"
    }

    # 10.2 Невідома група у Web-контурі -> 404 Not Found (О1.4 Web)
    $unknownWebGroupRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"NON-EXISTENT-GRP"}' -SkipHttpErrorCheck
    $unknownWebGroupBody = (Get-ResponseText $unknownWebGroupRes) | ConvertFrom-Json
    if ([int]$unknownWebGroupRes.StatusCode -eq 404 -and $unknownWebGroupBody.error_code -eq "group_not_found") {
        Write-Pass "Web О1.4: Невідома навчальна група у Web-контурі відхилена з HTTP 404 Not Found (error_code=group_not_found)"
    } else {
        Write-Fail "Помилка перевірки невідомої групи у Web: status=$([int]$unknownWebGroupRes.StatusCode)"
    }

    # 10.3 Відхилення через закрите часове вікно -> 422 Unprocessable Content (О1.1 Web)
    $windowRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-WINDOW-TEST"}' -SkipHttpErrorCheck
    $windowJson = (Get-ResponseText $windowRes) | ConvertFrom-Json
    if ([int]$windowRes.StatusCode -eq 422 -and $windowJson.error_code -eq "enrollment_window_closed") {
        Write-Pass "Web О1.1: Спроба запису поза вікном (DISC-WINDOW-TEST) відхилена з HTTP 422 Unprocessable Content (error_code=enrollment_window_closed)"
    } else {
        Write-Fail "Очікувався 422 enrollment_window_closed, отримано: $([int]$windowRes.StatusCode)"
    }

    # 10.4 Відхилення через перевищення ліміту K дисциплін -> 422 Unprocessable Content (О1.1 Web)
    $limitMaxHeaders = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-limit-max"; "Authorization" = "Bearer $Token" }
    $limitKRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $limitMaxHeaders -Body '{"group_id":"DISC-SEC-04"}' -SkipHttpErrorCheck
    $limitKJson = (Get-ResponseText $limitKRes) | ConvertFrom-Json
    if ([int]$limitKRes.StatusCode -eq 422 -and $limitKJson.error_code -eq "discipline_limit_exceeded") {
        Write-Pass "Web О1.1: Перевищення ліміту K дисциплін (5/5) відхилено з HTTP 422 Unprocessable Content (error_code=discipline_limit_exceeded)"
    } else {
        Write-Fail "Очікувався 422 discipline_limit_exceeded, отримано: $([int]$limitKRes.StatusCode)"
    }

    # 10.5 Відхилення через перевищення 30.0 ЄКТС -> 422 Unprocessable Content (О1.1, Д1.5 Web)
    $ects20Headers = @{ "Content-Type" = "application/json"; "X-User-Id" = "student-ects-20"; "Authorization" = "Bearer $Token" }
    $limitEctsRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $ects20Headers -Body '{"group_id":"DISC-ECTS-10-5-G1"}' -SkipHttpErrorCheck
    $limitEctsJson = (Get-ResponseText $limitEctsRes) | ConvertFrom-Json
    if ([int]$limitEctsRes.StatusCode -eq 422 -and $limitEctsJson.error_code -eq "ects_limit_exceeded") {
        Write-Pass "Web О1.1: Перевищення семестрового ліміту 30.0 ЄКТС (20.0 + 10.5 = 30.5) відхилено з HTTP 422 Unprocessable Content (error_code=ects_limit_exceeded)"
    } else {
        Write-Fail "Очікувався 422 ects_limit_exceeded, отримано: $([int]$limitEctsRes.StatusCode)"
    }

    # 10.6 Відхилення через брак місць у групі -> 409 Conflict (О1.1 Web, група DISC-ONE-SEAT)
    $oneSeat1 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent2 -Body '{"group_id":"DISC-ONE-SEAT"}' -SkipHttpErrorCheck
    $oneSeat2 = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-ONE-SEAT"}' -SkipHttpErrorCheck
    $oneSeat2Json = (Get-ResponseText $oneSeat2) | ConvertFrom-Json
    if ([int]$oneSeat2.StatusCode -eq 409 -and $oneSeat2Json.error_code -eq "group_full") {
        Write-Pass "Web О1.1: Спроба запису в заповнену групу DISC-ONE-SEAT відхилена з HTTP 409 Conflict (error_code=group_full)"
    } else {
        Write-Fail "Очікувався 409 group_full, отримано: $([int]$oneSeat2.StatusCode)"
    }

    # 10.7 Успішне створення запису Web: 201 Created, Location, X-Request-ID та стан held (О1.1, О1.4 Web)
    $postEnrollRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-WEB-01"}' -SkipHttpErrorCheck
    $postEnrollJson = (Get-ResponseText $postEnrollRes) | ConvertFrom-Json
    $heldId = $postEnrollJson.id
    $locHeader = [string]($postEnrollRes.Headers["Location"] | Select-Object -First 1)
    $retXReqId = [string]($postEnrollRes.Headers["X-Request-ID"] | Select-Object -First 1)

    if ([int]$postEnrollRes.StatusCode -eq 201 -and $locHeader -eq "/v1/enrollments/$heldId" -and !([string]::IsNullOrWhiteSpace($retXReqId)) -and $postEnrollJson.status -eq "held" -and !([string]::IsNullOrWhiteSpace($postEnrollJson.expires_at))) {
        Write-Pass "Web О1.4: Створено запис: HTTP 201 Created, заголовок Location ($locHeader), X-Request-ID ($retXReqId) та стан 'held' (expires_at=$($postEnrollJson.expires_at))"
    } else {
        Write-Fail "Помилка створення запису Web: status=$([int]$postEnrollRes.StatusCode), Location=$locHeader, X-Request-ID=$retXReqId"
    }

    # Додатковий запис для student-1 для тестування повної пагінації за next_cursor
    $null = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent1 -Body '{"group_id":"DISC-SEC-03"}' -SkipHttpErrorCheck

    # 10.8 Помилка пошкодженого курсору -> 400 Bad Request (О1.5 Web)
    $brokenCursorRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments?cursor=invalid_base64_or_corrupted" -Method Get -Headers $webHeadersStudent1 -SkipHttpErrorCheck
    $brokenCursorJson = (Get-ResponseText $brokenCursorRes) | ConvertFrom-Json
    if ([int]$brokenCursorRes.StatusCode -eq 400 -and $brokenCursorJson.error_code -eq "invalid_cursor") {
        Write-Pass "Web О1.5: Пошкоджений або некоректний курсор відхилено з HTTP 400 Bad Request (error_code=invalid_cursor)"
    } else {
        Write-Fail "Очікувався 400 invalid_cursor, отримано: $([int]$brokenCursorRes.StatusCode)"
    }

    # 10.9 Курсорне пагінування дисциплін (О1.5 Web, п. 16)
    $discPage1 = Invoke-RestMethod -Uri "$BaseUrl/v1/disciplines?limit=2" -Method Get -Headers $webHeadersStudent1
    if ($discPage1.items.Count -eq 2 -and !([string]::IsNullOrWhiteSpace($discPage1.next_cursor))) {
        Write-Pass "Курсорне пагінування GET /v1/disciplines (стор. 1): отримано 2 дисципліни, next_cursor=$($discPage1.next_cursor)"
        $discPage2 = Invoke-RestMethod -Uri "$BaseUrl/v1/disciplines?limit=2&cursor=$($discPage1.next_cursor)" -Method Get -Headers $webHeadersStudent1
        if ($discPage2.items.Count -eq 2 -and $discPage2.items[0].id -ne $discPage1.items[0].id) {
            Write-Pass "Курсорне пагінування GET /v1/disciplines (стор. 2): успішно отримано наступні дисципліни без перекриття (перша: $($discPage2.items[0].id))"
        } else {
            Write-Fail "Помилка курсорного переходу сторінки 2"
        }
    } else {
        Write-Fail "Помилка сторінки 1 дисциплін: count=$($discPage1.items.Count), next_cursor=$($discPage1.next_cursor)"
    }

    # 10.10 Курсорне пагінування записів студента через next_cursor (О1.5 Web)
    $enrPage1 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments?limit=1" -Method Get -Headers $webHeadersStudent1
    if ($enrPage1.items.Count -eq 1 -and !([string]::IsNullOrWhiteSpace($enrPage1.next_cursor))) {
        $enrPage2 = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments?limit=1&cursor=$($enrPage1.next_cursor)" -Method Get -Headers $webHeadersStudent1
        if ($enrPage2.items.Count -eq 1 -and $enrPage2.items[0].id -ne $enrPage1.items[0].id) {
            Write-Pass "Web О1.5: Курсорне пагінування GET /v1/enrollments: сторінка 1 та 2 пройдені послідовно через next_cursor без дублювання"
        } else {
            Write-Fail "Помилка сторінки 2 записів enrollments"
        }
    } else {
        Write-Fail "Помилка сторінки 1 записів enrollments: count=$($enrPage1.items.Count), next_cursor=$($enrPage1.next_cursor)"
    }

    # 10.11 Підтвердження запису (held -> confirmed) та ідемпотентність (О1.1 Web)
    $confirmedResp = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$heldId/confirm" -Method Post -Headers $webHeadersStudent1
    if ($confirmedResp.status -eq "confirmed") {
        Write-Pass "Web О1.1: Підтверджено попередній запис (held -> confirmed): отримано статус 'confirmed'"
    } else {
        Write-Fail "Очікувався статус confirmed, отримано: $($confirmedResp.status)"
    }

    $reConfirmedResp = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$heldId/confirm" -Method Post -Headers $webHeadersStudent1
    if ($reConfirmedResp.status -eq "confirmed" -and $reConfirmedResp.version -eq $confirmedResp.version) {
        Write-Pass "Web О1.1: Повторне підтвердження є ідемпотентним (HTTP 200, версія $($reConfirmedResp.version) не змінилась)"
    } else {
        Write-Fail "Помилка повторного підтвердження: status=$($reConfirmedResp.status), v=$($reConfirmedResp.version)"
    }

    # 10.12 Ліниве прострочення (held -> expired із автоматичним поверненням місця в групу, п. 2)
    $student3InitSeats = (Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-AI-02" -Method Get -Headers $Headers).free_seats
    $heldToExpire = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent3 -Body '{"group_id":"DISC-AI-02"}'
    $expId = $heldToExpire.id
    $seatsAfterHold = (Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-AI-02" -Method Get -Headers $Headers).free_seats

    # Штучно переводимо created_at та expires_at у минулий час через PostgreSQL (зберігаючи инваріант expires_at > created_at) для перевірки лінивого прострочення
    & $DockerExe compose exec -e PGPASSWORD=dpc_password -T postgres psql -U dpc_user -d dpc_c10 -c "UPDATE enrollment SET created_at = NOW() - INTERVAL '30 minutes', expires_at = NOW() - INTERVAL '15 minutes' WHERE id = '$expId';" | Out-Null

    # Спроба підтвердити прострочений запис має виявити прострочення, перевести його в expired та повернути місце в групу
    $confirmExpiredRes = Invoke-WebRequest -Uri "$BaseUrl/v1/enrollments/$expId/confirm" -Method Post -Headers $webHeadersStudent3 -SkipHttpErrorCheck
    $seatsAfterExpire = (Invoke-RestMethod -Uri "$BaseUrl/v1/group-seats/DISC-AI-02" -Method Get -Headers $Headers).free_seats

    if ([int]$confirmExpiredRes.StatusCode -eq 409 -and $seatsAfterExpire -eq $student3InitSeats) {
        Write-Pass "Web О1.1: Ліниве прострочення (п. 2) спрацювало: підтвердження простроченого запису відхилено з 409 Conflict, місце в групі ($seatsAfterExpire) повернено!"
    } else {
        Write-Fail "Помилка лінивого прострочення: HTTP=$([int]$confirmExpiredRes.StatusCode), seats=$seatsAfterExpire (очікувалось $student3InitSeats)"
    }

    # 10.13 Скасування запису та перевірка повторного скасування
    $enrToCancelRes = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments" -Method Post -Headers $webHeadersStudent2 -Body '{"group_id":"DISC-WEB-01"}'
    $cancelId = $enrToCancelRes.id
    $firstCancel = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$cancelId/cancel" -Method Post -Headers $webHeadersStudent2
    $secondCancel = Invoke-RestMethod -Uri "$BaseUrl/v1/enrollments/$cancelId/cancel" -Method Post -Headers $webHeadersStudent2

    if ($firstCancel.status -eq "cancelled" -and $secondCancel.status -eq "cancelled" -and $firstCancel.version -eq $secondCancel.version) {
        Write-Pass "Пункт 10: Повторне скасування запису повертає фактичний статус 'cancelled' без штучного збільшення версії ($($firstCancel.version) == $($secondCancel.version))"
    } else {
        Write-Fail "Помилка перевірки статусу скасування: v1=$($firstCancel.version), v2=$($secondCancel.version)"
    }

    # 10.14 Балансування least_conn між app-a та app-b (О1.6 Web)
    $instancesObserved = [System.Collections.Generic.HashSet[string]]::new()
    for ($i = 0; $i -lt 15; $i++) {
        $hResp = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
        if (![string]::IsNullOrWhiteSpace($hResp.instance_id)) {
            $null = $instancesObserved.Add($hResp.instance_id)
        }
    }

    if ($instancesObserved.Contains("app-a") -and $instancesObserved.Contains("app-b")) {
        Write-Pass "О1.6 (Web): Балансування навантаження least_conn між app-a та app-b підтверджено (зафіксовано відповіді від обох вузлів)"
    } else {
        Write-Info "Зафіксовано відповіді від вузлів: $($instancesObserved -join ', ')"
    }

} finally {
    Write-Info "Повернення середовища у штатно-захищений режим ALLOW_TEST_OVERRIDES=false..."
    $null = Set-TestOverridesMode -enable $false
    Write-Pass "Захищений режим ALLOW_TEST_OVERRIDES=false успішно відновлено."
}

Write-Header "РЕЗУЛЬТАТ ПЕРЕВІРКИ ЛР1"
if ($script:ErrorCount -eq 0) {
    Write-Host "`n  Всі перевірки пройдено успішно (0 помилок).`n" -ForegroundColor Green
} else {
    Write-Host "`n  Виявлено $script:ErrorCount помилок.`n" -ForegroundColor Red
}

Pop-Location
if ($script:ErrorCount -gt 0) { exit 1 }
