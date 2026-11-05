# scripts/measure_metrics_500.ps1
# Реальне емпіричне вимірювання регламентної вибірки N=500 для Розділу 7.2 README.md

param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$Token = "secret-c10-token-2026"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

$DockerExe = "docker"
if (-not (Get-Command "docker" -ErrorAction SilentlyContinue)) {
    if (Test-Path "C:\Program Files\Docker\Docker\resources\bin\docker.exe") {
        $DockerExe = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
    }
}

try {
    Write-Host "=== Крок 1: Додавання 500 тестових студентів та налаштування групи DISC-DPC-01 (150 місць) ===" -ForegroundColor Cyan
    & $DockerExe compose exec -T postgres psql -U dpc_user -d dpc_c10 -c "
    INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
    SELECT
        'student-perf-' || LPAD(i::text, 3, '0'),
        'perf' || i || '@test.inf.ua',
        'Perf Student ' || i,
        5, 0, 0.0
    FROM generate_series(1, 500) AS i
    ON CONFLICT (id) DO NOTHING;
    UPDATE discipline_group SET seats = 150, available = 150 WHERE id = 'DISC-DPC-01';
    " | Out-Null

    Write-Host "=== Крок 2: Увімкнення ALLOW_TEST_OVERRIDES=true на app-a та app-b ===" -ForegroundColor Cyan
    $env:ALLOW_TEST_OVERRIDES = "true"
    & $DockerExe compose up -d app-a app-b | Out-Null
    & $DockerExe compose restart nginx | Out-Null
    Remove-Item Env:ALLOW_TEST_OVERRIDES -ErrorAction SilentlyContinue

    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 300
        try {
            $m = Invoke-RestMethod -Uri "$BaseUrl/metrics" -Method Get -Headers @{ "Authorization" = "Bearer $Token" } -TimeoutSec 2
            if ($null -ne $m.configuration -and [bool]$m.configuration.allow_test_overrides -eq $true) {
                Write-Host "app-a готовий з ALLOW_TEST_OVERRIDES=true" -ForegroundColor Green
                break
            }
        } catch { }
    }

    Write-Host "=== Крок 3: Запуск C# вимірювача метрик на вибірці N=500 ===" -ForegroundColor Cyan
    & dotnet run --project tools/MetricsMeasurer/MetricsMeasurer.csproj -- $BaseUrl $Token
    if ($LASTEXITCODE -ne 0) {
        throw "Вимірювач завершився з помилкою $LASTEXITCODE"
    }
}
finally {
    Write-Host "=== Крок 4: Відновлення конфігурації БД та ALLOW_TEST_OVERRIDES=false ===" -ForegroundColor Cyan
    & $DockerExe compose exec -T postgres psql -U dpc_user -d dpc_c10 -c "
    UPDATE discipline_group SET seats = 30, available = 30 WHERE id = 'DISC-DPC-01';
    " | Out-Null
    $env:ALLOW_TEST_OVERRIDES = "false"
    & $DockerExe compose up -d app-a app-b | Out-Null
    & $DockerExe compose restart nginx | Out-Null
    Remove-Item Env:ALLOW_TEST_OVERRIDES -ErrorAction SilentlyContinue
    Pop-Location
}
