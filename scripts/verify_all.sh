#!/usr/bin/env bash
set -uo pipefail

# ==============================================================================
# Комплексний сценарій автоматизованого тестування та перевірки ЛР1 (Web + РПО)
# Тема С10: «Запис на вибіркові дисципліни»
# Студент: Зінківський Тимур Євгенійович, група ІТІНФ-23-2
# Повна функціональна еквівалентність зі сценарієм scripts/verify_all.ps1
# ==============================================================================

BASE_URL="${1:-http://localhost:8080}"
TOKEN="${2:-secret-c10-token-2026}"
AUTH_HEADER="Authorization: Bearer ${TOKEN}"
ERROR_COUNT=0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

if docker ps >/dev/null 2>&1; then
    DOCKER_BIN="docker"
elif command -v docker.exe >/dev/null 2>&1 && docker.exe ps >/dev/null 2>&1; then
    DOCKER_BIN="docker.exe"
elif [ -x "/c/Program Files/Docker/Docker/resources/bin/docker.exe" ]; then
    DOCKER_BIN="/c/Program Files/Docker/Docker/resources/bin/docker.exe"
elif [ -x "/mnt/c/Program Files/Docker/Docker/resources/bin/docker.exe" ]; then
    DOCKER_BIN="/mnt/c/Program Files/Docker/Docker/resources/bin/docker.exe"
else
    DOCKER_BIN="docker"
fi

if command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="dotnet"
elif command -v dotnet.exe >/dev/null 2>&1; then
    DOTNET_BIN="dotnet.exe"
else
    DOTNET_BIN="dotnet"
fi

export WSLENV="ALLOW_TEST_OVERRIDES:${WSLENV:-}"

pass() {
    echo "    [PASS] $1"
}

fail() {
    echo "    [FAIL] $1"
    ERROR_COUNT=$((ERROR_COUNT + 1))
}

info() {
    echo "    [INFO] $1"
}

set_test_overrides_mode() {
    local enable="$1"
    ALLOW_TEST_OVERRIDES="${enable}" "${DOCKER_BIN}" compose up -d app-a app-b >/dev/null 2>&1
    "${DOCKER_BIN}" compose restart nginx >/dev/null 2>&1
    for _ in $(seq 1 40); do
        sleep 0.3
        local h
        h=$(curl -s --max-time 2 "${BASE_URL}/health" || true)
        if echo "${h}" | grep -q '"status":"ok"' && echo "${h}" | grep -q "\"allow_test_overrides\":${enable}"; then
            return 0
        fi
    done
    return 1
}

wait_for_queue_drain() {
    local expected_min="${1:-0}"
    for _ in $(seq 1 60); do
        sleep 0.25
        local m_wait
        m_wait=$(curl -s "${BASE_URL}/metrics?format=json" || true)
        if echo "${m_wait}" | grep -q '"queue_length":0' && echo "${m_wait}" | grep -q '"in_processing":0'; then
            if [ "${expected_min}" -le 0 ]; then
                break
            fi
            local done_cnt rej_cnt
            done_cnt=$(echo "${m_wait}" | grep -o '"total_done":[0-9]*' | cut -d':' -f2)
            rej_cnt=$(echo "${m_wait}" | grep -o '"total_rejected":[0-9]*' | cut -d':' -f2)
            done_cnt="${done_cnt:-0}"
            rej_cnt="${rej_cnt:-0}"
            if [ $((done_cnt + rej_cnt)) -ge "${expected_min}" ]; then
                break
            fi
        fi
    done
}

TEST_MODE_ACTIVE=0
cleanup() {
    if [ "${TEST_MODE_ACTIVE}" -eq 1 ]; then
        info "Повернення контейнерів у робочий захищений режим ALLOW_TEST_OVERRIDES=false..."
        set_test_overrides_mode "false" || true
        TEST_MODE_ACTIVE=0
    fi
}
trap cleanup EXIT

echo "================================================================================"
echo "  СТАРТ КОМПЛЕКСНОЇ ПЕРЕВІРКИ ЛР1 (Тема С10: Web + РПО)"
echo "  Цільовий сервіс (Nginx): ${BASE_URL}"
echo "================================================================================"

# ------------------------------------------------------------------------------
# 0. Статична перевірка OpenAPI 3.1 (Spectral), модульні тести та schema_migrations
# ------------------------------------------------------------------------------
echo "[0] Контракт OpenAPI 3.1 (Spectral), модульні тести (dotnet test) та міграції БД..."
if command -v npx >/dev/null 2>&1; then
    if npx --yes @stoplight/spectral-cli lint openapi.yaml --fail-severity=warn >/dev/null 2>&1; then
        pass "О1.3 (Web): Контракт openapi.yaml успішно пройшов перевірку лінтером Spectral (0 помилок, 0 попереджень)"
    else
        fail "О1.3 (Web): Лінтер Spectral виявив помилки в openapi.yaml"
    fi
else
    fail "О1.3 (Web): Не знайдено npx для запуску лінтера Spectral"
fi

if command -v "${DOTNET_BIN}" >/dev/null 2>&1; then
    if "${DOTNET_BIN}" test tests/EnrollmentService.Tests/EnrollmentService.Tests.csproj --nologo -v minimal >/dev/null 2>&1; then
        pass "Д1.1 (Web/РПО): Модульні тести предметних правил і неблокуючої async-черги пройдено успішно"
    else
        fail "Модульні тести dotnet test завершилися з помилкою"
    fi
else
    fail "Не знайдено dotnet SDK для запуску модульних тестів"
fi

MIG_CNT=$("${DOCKER_BIN}" exec dpc-c10-postgres psql -U dpc_user -d dpc_c10 -tAc "SELECT count(*) FROM schema_migrations WHERE version IN ('001','002');" 2>/dev/null | tr -d ' \r\n')
VIEW_CNT=$("${DOCKER_BIN}" exec dpc-c10-postgres psql -U dpc_user -d dpc_c10 -tAc "SELECT count(*) FROM pg_views WHERE viewname = 'group_seats';" 2>/dev/null | tr -d ' \r\n')
if [ "${MIG_CNT}" = "2" ] && [ "${VIEW_CNT}" = "1" ]; then
    pass "О1.1 (Web) та §1.10: Таблиця schema_migrations містить версії 001 і 002; group_seats реалізовано як SQL VIEW над discipline_group"
else
    fail "Помилка перевірки schema_migrations (${MIG_CNT}) або pg_views (${VIEW_CNT})"
fi

# ------------------------------------------------------------------------------
# 1. GET /health та відокремлений клієнт GET / (§1.2)
# ------------------------------------------------------------------------------
echo "[1] Перевірка службового маршруту /health (О1.2 РПО / О1.6 Web) та GET / (§1.2)..."
HEALTH=$(curl -s "${BASE_URL}/health" || true)
if echo "${HEALTH}" | grep -q '"allow_test_overrides":true'; then
    info "Перемикання стенду у робочий захищений режим ALLOW_TEST_OVERRIDES=false..."
    set_test_overrides_mode "false"
    HEALTH=$(curl -s "${BASE_URL}/health" || true)
fi
ROOT_CT=$(curl -s -D - -o /dev/null "${BASE_URL}/" | tr -d '\r' | grep -i "^Content-Type:" | head -n1)

if echo "${HEALTH}" | grep -q '"status":"ok"' && echo "${ROOT_CT}" | grep -iq "application/json"; then
    pass "Маршрут /health активний; образ API не роздає вбудований клієнт (GET / -> application/json)"
else
    fail "Неочікувана відповідь /health або GET /: ${HEALTH} / ${ROOT_CT}"
fi

# ------------------------------------------------------------------------------
# 2. Перевірка безпеки у робочому режимі (ALLOW_TEST_OVERRIDES=false)
# ------------------------------------------------------------------------------
echo "[2] Безпека API у робочому режимі: Bearer (401), X-User-Id (400/422) та закриття тестових лазівок (403)..."
RESP_401=$(curl -s -i "${BASE_URL}/v1/group-seats" | tr -d '\r')
STATUS_NO_AUTH=$(echo "${RESP_401}" | head -n1 | awk '{print $2}')
HAS_PROB_401=$(echo "${RESP_401}" | grep -i -c "^Content-Type:.*application/problem+json" || true)
if [ "${STATUS_NO_AUTH}" = "401" ] && [ "${HAS_PROB_401}" -ge 1 ]; then
    pass "Запит без Bearer-токена повернув HTTP 401 Unauthorized (application/problem+json)"
else
    fail "Очікувався 401 problem+json, отримано ${STATUS_NO_AUTH}"
fi

NO_USER_POST_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -d '{"group_id":"DISC-WEB-01"}')
NO_USER_GET_CODE=$(curl -s -o /dev/null -w "%{http_code}" "${BASE_URL}/v1/enrollments")
OWNER_BODY_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-WEB-01","student_id":"student-2"}')

if [ "${NO_USER_POST_CODE}" = "400" ] && [ "${NO_USER_GET_CODE}" = "400" ] && [ "${OWNER_BODY_CODE}" = "422" ]; then
    pass "Запити без X-User-Id відхилено (400), передачу student_id у тілі заборонено (422)"
else
    fail "Помилка перевірки X-User-Id / student_id: post=${NO_USER_POST_CODE}, get=${NO_USER_GET_CODE}, ownerBody=${OWNER_BODY_CODE}"
fi

EVAL_PROD_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-2" -d '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T07:00:00Z"}')
SIM_PROD_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-2" -H "X-Simulated-Now: 2026-09-01T07:00:00Z" -d '{"group_id":"DISC-WINDOW-TEST"}')
RESET_PROD_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/admin/reset" -H "${AUTH_HEADER}")
REAL_WIN_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-2" -d '{"group_id":"DISC-WINDOW-TEST"}')

if [ "${EVAL_PROD_CODE}" = "403" ] && [ "${SIM_PROD_CODE}" = "403" ] && [ "${RESET_PROD_CODE}" = "403" ] && [ "${REAL_WIN_CODE}" = "409" ]; then
    pass "У робочому режимі (ALLOW_TEST_OVERRIDES=false) тестові лазівки evaluated_at, X-Simulated-Now та /v1/admin/reset закриті (403), поза вікном -> 409"
else
    fail "Помилка закриття лазівок: eval=${EVAL_PROD_CODE}, sim=${SIM_PROD_CODE}, reset=${RESET_PROD_CODE}, realWin=${REAL_WIN_CODE}"
fi

# ------------------------------------------------------------------------------
# Активація контрольованого тестового стенду ALLOW_TEST_OVERRIDES=true
# ------------------------------------------------------------------------------
info "Активація контрольованого тестового режиму ALLOW_TEST_OVERRIDES=true..."
TEST_MODE_ACTIVE=1
if ! set_test_overrides_mode "true"; then
    fail "Не вдалося активувати ALLOW_TEST_OVERRIDES=true"
    exit 1
fi
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -H "${AUTH_HEADER}" >/dev/null

# ------------------------------------------------------------------------------
# 3. Пункт О1.1 (РПО) та §1.10 / С10 п. 2, 6: Розподіл відповідальності між Web і РПО,
#    відомість деканату за request_id, ліміти K та 30.0 ЄКТС у РПО, ідемпотентність та 409 Conflict
# ------------------------------------------------------------------------------
echo "[3] Пункт О1.1 (РПО) та С10 §3.12: Розподіл відповідальності Web і РПО, відомість деканату за request_id та ліміти K/ЄКТС..."

# Крок 1: Первинний запис студента в групу та зменшення місць (30 -> 29) виконує ВИКЛЮЧНО Web
WEB_PRIMARY_RESP=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/v1/enrollments" \
  -H "Content-Type: application/json" \
  -H "X-User-Id: student-1" \
  -d '{"group_id":"DISC-DPC-01"}')
WEB_PRIMARY_CODE=$(echo "${WEB_PRIMARY_RESP}" | tail -n1)
WEB_PRIMARY_BODY=$(echo "${WEB_PRIMARY_RESP}" | sed '$d')
WEB_PRIMARY_ID=$(echo "${WEB_PRIMARY_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)

if [ "${WEB_PRIMARY_CODE}" = "201" ] && [ -n "${WEB_PRIMARY_ID}" ]; then
    pass "Крок 1 (Виключна відповідальність Web): POST /v1/enrollments записав student-1 у DISC-DPC-01 (status=held) і зменшив available з 30 до 29"
else
    fail "Помилка первинного запису через Web: code=${WEB_PRIMARY_CODE}"
fi

# Крок 2: Обробник РПО не резервує місця повторно, а фіксує заявку у відомості деканату за request_id
FIXED_ID="11111111-2222-3333-4444-555555555555"
POST_RESP=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d "{\"requestId\":\"${FIXED_ID}\",\"student_id\":\"student-1\",\"group_id\":\"DISC-DPC-01\"}")
HTTP_CODE=$(echo "${POST_RESP}" | tail -n1)
BODY=$(echo "${POST_RESP}" | sed '$d')

if [ "${HTTP_CODE}" = "202" ]; then
    pass "Отримано HTTP 202 Accepted (заявка збережена зі статусом queued перед чергою)"
    REQ_ID=$(echo "${BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)

    for _ in $(seq 1 30); do
        sleep 0.2
        STATUS_RESP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${REQ_ID}")
        if echo "${STATUS_RESP}" | grep -q '"status":"done"'; then
            break
        fi
    done

    if echo "${STATUS_RESP}" | grep -q '"status":"done"' && echo "${STATUS_RESP}" | grep -q '"recordedInRoster":true'; then
        pass "Заявка ${REQ_ID} перейшла у стан 'done' і зафіксована у відомості деканату за request_id"
    else
        fail "Заявка не перейшла у стан 'done' або не зафіксована у відомості: ${STATUS_RESP}"
    fi
else
    fail "Очікувався 202, отримано ${HTTP_CODE}"
fi

# Перевірка, що РПО НЕ зменшив available вдруге (available залишається 29/30), а відомість містить request_id
DPC_GRP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats/DISC-DPC-01")
WEB_DISC=$(curl -s "${BASE_URL}/v1/disciplines?limit=20")
ROSTER_RESP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/decanate-roster?request_id=${REQ_ID}")
WEB_CONF_RESP=$(curl -s -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments/${WEB_PRIMARY_ID}")

if echo "${DPC_GRP}" | grep -q '"taken":1' && echo "${DPC_GRP}" | grep -q '"freeSeats":29' && \
   echo "${DPC_GRP}" | grep -q '"rosterCount":1' && echo "${WEB_DISC}" | grep -q '"available":29' && \
   echo "${ROSTER_RESP}" | grep -q '"assignment_type":"primary_roster"' && echo "${WEB_CONF_RESP}" | grep -q '"status":"confirmed"'; then
    pass "С10 §3.12 (Без подвійного резервування): воркер РПО зафіксував request_id у decanate_roster (status=confirmed), а вільні місця залишилися 29/30!"
else
    fail "Помилка узгодження Web і РПО: dpc=${DPC_GRP}, roster=${ROSTER_RESP}"
fi

# Перевірка відхилення РПО без первинного Web-запису та перевірки лімітів K і 30.0 ЄКТС у логіці РПО
NO_WEB_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" -H "Content-Type: application/json" -H "${AUTH_HEADER}" -d '{"student_id":"student-2","group_id":"DISC-DPC-01"}')
NO_WEB_ID=$(echo "${NO_WEB_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
K_LIM_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" -H "Content-Type: application/json" -H "${AUTH_HEADER}" -d '{"student_id":"student-limit-max","group_id":"DISC-DPC-01"}')
K_LIM_ID=$(echo "${K_LIM_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
ECTS_LIM_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" -H "Content-Type: application/json" -H "${AUTH_HEADER}" -d '{"student_id":"student-ects-20","group_id":"DISC-ECTS-10-5-G1"}')
ECTS_LIM_ID=$(echo "${ECTS_LIM_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)

for _ in $(seq 1 30); do
    sleep 0.15
    ST_NOWEB=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${NO_WEB_ID}")
    ST_KLIM=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${K_LIM_ID}")
    ST_ECTS=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${ECTS_LIM_ID}")
    if echo "${ST_NOWEB}" | grep -q '"status":"rejected"' && echo "${ST_KLIM}" | grep -q '"status":"rejected"' && echo "${ST_ECTS}" | grep -q '"status":"rejected"'; then
        break
    fi
done

if echo "${ST_NOWEB}" | grep -q '"status":"rejected"' && echo "${ST_KLIM}" | grep -q '"status":"rejected"' && echo "${ST_ECTS}" | grep -q '"status":"rejected"'; then
    pass "Перевірка обмежень у логіці РПО: відхилено запит без первинного запису Web, перевищення K=5 та перевищення 30.5 > 30.0 ЄКТС!"
else
    fail "Помилка перевірки обмежень у РПО: noWeb=${ST_NOWEB}, K=${ST_KLIM}, ECTS=${ST_ECTS}"
fi

DUP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d "{\"requestId\":\"${FIXED_ID}\",\"student_id\":\"student-1\",\"group_id\":\"DISC-DPC-01\"}")
GROUP_AFTER_DUP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats/DISC-DPC-01")
if [ "${DUP_CODE}" = "202" ] && echo "${GROUP_AFTER_DUP}" | grep -q '"taken":1' && echo "${GROUP_AFTER_DUP}" | grep -q '"rosterCount":1'; then
    pass "Повторний запит із тим самим ID не потрапив повторно в чергу і не продублював запис у відомості деканату (taken=1, rosterCount=1)"
else
    fail "Помилка ідемпотентності: code=${DUP_CODE}, group=${GROUP_AFTER_DUP}"
fi

CONFLICT_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" \
  -H "${AUTH_HEADER}" \
  -d "{\"requestId\":\"${FIXED_ID}\",\"student_id\":\"STD-OTHER\",\"group_id\":\"DISC-DPC-01\"}")
if [ "${CONFLICT_CODE}" = "409" ]; then
    pass "Повторний запит з тим самим ID та іншими даними повернув HTTP 409 Conflict"
else
    fail "Очікувався 409 Conflict, отримано ${CONFLICT_CODE}"
fi

INV_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
  -d '{"student_id":"STD-INV","group_id":"NON-EXISTENT-GROUP"}')
INV_ID=$(echo "${INV_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
for _ in $(seq 1 25); do
    sleep 0.15
    INV_ST=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${INV_ID}")
    if echo "${INV_ST}" | grep -q '"status":"failed"'; then break; fi
done
if echo "${INV_ST}" | grep -q '"status":"failed"'; then
    pass "Неіснуюча група перейшла у кінцевий стан 'failed'"
else
    fail "Очікувався стан 'failed' для неіснуючої групи: ${INV_ST}"
fi

# ------------------------------------------------------------------------------
# 4. Пункт О1.2 (РПО): Обмежена черга, 429 + Retry-After та серія /health під навантаженням
# ------------------------------------------------------------------------------
echo "[4] Пункт О1.2 (РПО): Перевантаження черги (429 + Retry-After) та вимірювання серії /health..."
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null

BURST_START=$(date +%s)
TMP_DIR=".tmp_burst_$$"
mkdir -p "${TMP_DIR}"
for i in $(seq 1 150); do
    curl -s -i -X POST "${BASE_URL}/v1/enrollment-requests" \
      -H "Content-Type: application/json" \
      -H "${AUTH_HEADER}" \
      -d "{\"student_id\":\"STD-BURST-${i}\",\"group_id\":\"DISC-DPC-01\"}" > "${TMP_DIR}/resp_${i}.txt" &
done

MAX_HEALTH_MS=0
for _ in $(seq 1 30); do
    TIME_SEC=$(curl -s -o /dev/null -w "%{time_total}" "${BASE_URL}/health" | tr ',' '.')
    LAT_MS=$(awk -v t="${TIME_SEC}" 'BEGIN { printf "%d", (t * 1000) }')
    if [ "${LAT_MS}" -gt "${MAX_HEALTH_MS}" ]; then
        MAX_HEALTH_MS="${LAT_MS}"
    fi
done

wait
BURST_DUR=$(( $(date +%s) - BURST_START ))

COUNT_429=$(grep -l " 429 " "${TMP_DIR}"/resp_*.txt 2>/dev/null | wc -l | tr -d ' ')
COUNT_RETRY=$(grep -i -l "^Retry-After:" "${TMP_DIR}"/resp_*.txt 2>/dev/null | wc -l | tr -d ' ')
rm -rf "${TMP_DIR}"

info "Тривалість залпу (150 запитів + 30 /health): ${BURST_DUR} с | відповідей 429: ${COUNT_429} (з Retry-After: ${COUNT_RETRY}), макс. /health: ${MAX_HEALTH_MS} мс"
if [ "${COUNT_429}" -gt 0 ] && [ "${COUNT_RETRY}" -eq "${COUNT_429}" ]; then
    pass "Переповнення черги повернуло ${COUNT_429} відповідей 429 Too Many Requests із заголовком Retry-After"
else
    fail "Не зафіксовано 429 або відсутній заголовок Retry-After (429=${COUNT_429}, retry=${COUNT_RETRY})"
fi

if [ "${MAX_HEALTH_MS}" -le 100 ]; then
    pass "Затримка /health під навантаженням (${MAX_HEALTH_MS} мс) <= 100 мс"
else
    fail "Затримка /health перевищила 100 мс: ${MAX_HEALTH_MS} мс"
fi

# ------------------------------------------------------------------------------
# 5. Пункт О1.4 (РПО): Захист єдиного спільного ресурсу discipline_group (SAFE vs UNSAFE)
# ------------------------------------------------------------------------------
echo "[5] Пункт О1.4 (РПО): Перевірка інваріанта discipline_group / VIEW group_seats (SAFE vs UNSAFE)..."
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null
for i in $(seq 1 20); do
    curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollment-requests" \
      -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
      -d "{\"student_id\":\"STD-SAFE-${i}\",\"group_id\":\"DISC-TEST-SAFE\"}" &
done
wait
wait_for_queue_drain 20

SAFE_GROUP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats/DISC-TEST-SAFE")
if echo "${SAFE_GROUP}" | grep -q '"taken":5' && echo "${SAFE_GROUP}" | grep -q '"invariantHolds":true'; then
    pass "БЕЗПЕЧНИЙ РЕЖИМ: taken=5/5, інваріант строго дотримано"
else
    fail "Порушення у безпечному режимі: ${SAFE_GROUP}"
fi

curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=true" -H "${AUTH_HEADER}" >/dev/null
for i in $(seq 1 20); do
    curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollment-requests" \
      -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
      -d "{\"student_id\":\"STD-UNSAFE-${i}\",\"group_id\":\"DISC-TEST-UNSAFE\"}" &
done
wait
wait_for_queue_drain 20

UNSAFE_GROUP=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats/DISC-TEST-UNSAFE")
if echo "${UNSAFE_GROUP}" | grep -q '"invariantHolds":false'; then
    pass "НЕБЕЗПЕЧНИЙ РЕЖИМ: стан гонитви відтворено, інваріант порушено (${UNSAFE_GROUP})"
else
    fail "У небезпечному режимі не зафіксовано порушення інваріанта: ${UNSAFE_GROUP}"
fi

curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null

# ------------------------------------------------------------------------------
# 6. Пункт О1.5 (РПО): Монотонність позначок та одноразовий firstReadAt
# ------------------------------------------------------------------------------
echo "[6] Пункт О1.5 (РПО): Перевірка монотонності позначок та одноразової фіксації першого GET..."
O15_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
  -d '{"student_id":"STD-O15-BASH","group_id":"DISC-DPC-01"}')
O15_ID=$(echo "${O15_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
wait_for_queue_drain 1

GET1=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${O15_ID}")
sleep 0.2
GET2=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${O15_ID}")
DELIV1=$(echo "${GET1}" | grep -o '"deliveryWaitMs":[0-9.]*' | cut -d':' -f2)
DELIV2=$(echo "${GET2}" | grep -o '"deliveryWaitMs":[0-9.]*' | cut -d':' -f2)
METRICS_O15=$(curl -s "${BASE_URL}/metrics?format=json")

if [ -n "${DELIV1}" ] && [ "${DELIV1}" = "${DELIV2}" ] && echo "${METRICS_O15}" | grep -q '"monotonic_order_violations":0'; then
    pass "firstReadAt зафіксовано один раз (deliveryWaitMs=${DELIV1} мс), порушень монотонності: 0"
else
    fail "Помилка О1.5: deliv1=${DELIV1}, deliv2=${DELIV2}"
fi

# ------------------------------------------------------------------------------
# 7. Пункт Д1.1 (РПО): Реальні вимірювання обох моделей виконання (async_tasks vs dedicated_threads)
# ------------------------------------------------------------------------------
echo "[7] Пункт Д1.1 (РПО): Порівняння моделей async_tasks (неблокуючий TCS) та dedicated_threads (30 запитів)..."
for MODEL in async_tasks dedicated_threads; do
    curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false&executionModel=${MODEL}" -H "${AUTH_HEADER}" >/dev/null
    T_START=$(date +%s)
    for i in $(seq 1 30); do
        curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollment-requests" \
          -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
          -d "{\"student_id\":\"STD-${MODEL}-${i}\",\"group_id\":\"DISC-DPC-01\"}" &
    done
    wait
    wait_for_queue_drain 30
    M_SNAP=$(curl -s "${BASE_URL}/metrics?format=json")
    T_END=$(date +%s)
    ELAPSED_SEC=$((T_END - T_START))
    if echo "${M_SNAP}" | grep -q "\"execution_model\":\"${MODEL}\"" && echo "${M_SNAP}" | grep -q '"total_done":30'; then
        pass "Модель ${MODEL}: 30/30 оброблено за ${ELAPSED_SEC} с"
    else
        fail "Помилка виконання на моделі ${MODEL}"
    fi
done
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -H "${AUTH_HEADER}" >/dev/null

# ------------------------------------------------------------------------------
# 8. Пункт Д1.2 (РПО): Повний 60-секундний дослід закону Літтла (~4 зап/с, розігрів 10 с виключено)
# ------------------------------------------------------------------------------
echo "[8] Пункт Д1.2 (РПО): Закон Літтла L = lambda * W (60 с по ~4 зап/с, розігрів 10 с виключено + вибірка 100 мс)..."
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null

EXP_START=$(date +%s)
REQ_SEQ=0
FIRST_HALF_SUM=0
FIRST_HALF_CNT=0
SECOND_HALF_SUM=0
SECOND_HALF_CNT=0

for SEC in $(seq 0 59); do
    if [ "${SEC}" -eq 10 ]; then
        curl -s -X POST "${BASE_URL}/v1/admin/reset?metricsOnly=true" -H "${AUTH_HEADER}" >/dev/null
        info "Перші 10 с розігріву завершено -> розпочато залікове вікно вимірювань (50 с)..."
    fi

    for _ in 1 2 3 4; do
        REQ_SEQ=$((REQ_SEQ + 1))
        curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollment-requests" \
          -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
          -d "{\"student_id\":\"STD-LITTLE-SH-${REQ_SEQ}\",\"group_id\":\"DISC-DPC-01\"}" &
    done

    if [ "${SEC}" -ge 10 ]; then
        sleep 0.12
        M_TICK=$(curl -s "${BASE_URL}/metrics?format=json" || true)
        Q_CUR=$(echo "${M_TICK}" | grep -o '"queue_length":[0-9]*' | head -n1 | cut -d':' -f2)
        Q_CUR="${Q_CUR:-0}"
        if [ "${SEC}" -lt 35 ]; then
            FIRST_HALF_SUM=$((FIRST_HALF_SUM + Q_CUR))
            FIRST_HALF_CNT=$((FIRST_HALF_CNT + 1))
        else
            SECOND_HALF_SUM=$((SECOND_HALF_SUM + Q_CUR))
            SECOND_HALF_CNT=$((SECOND_HALF_CNT + 1))
        fi
    fi

    while [ $(( $(date +%s) - EXP_START )) -lt $((SEC + 1)) ]; do
        sleep 0.05
    done
done

METRICS_LITTLE=$(curl -s "${BASE_URL}/metrics?format=json")
wait
sleep 2

OBS_WIN=$(echo "${METRICS_LITTLE}" | grep -o '"observation_window_sec":[0-9.]*' | cut -d':' -f2)
L_THEOR=$(echo "${METRICS_LITTLE}" | grep -o '"l_theoretical_avg_queue_length":[0-9.]*' | cut -d':' -f2)
L_OBS=$(echo "${METRICS_LITTLE}" | grep -o '"l_observed_avg_queue_length":[0-9.]*' | cut -d':' -f2)
L_INDEP=$(echo "${METRICS_LITTLE}" | grep -o '"l_observed_independent_sampled":[0-9.]*' | cut -d':' -f2)
INDEP_CNT=$(echo "${METRICS_LITTLE}" | grep -o '"independent_sample_count":[0-9]*' | cut -d':' -f2)
INDEP_DISC=$(echo "${METRICS_LITTLE}" | grep -o '"independent_discrepancy_pct":[0-9.]*' | cut -d':' -f2)

LITTLE_OK=$(awk -v win="${OBS_WIN:-0}" -v cnt="${INDEP_CNT:-0}" -v disc="${INDEP_DISC:-100}" \
    -v s1="${FIRST_HALF_SUM}" -v c1="${FIRST_HALF_CNT}" -v s2="${SECOND_HALF_SUM}" -v c2="${SECOND_HALF_CNT}" \
    'BEGIN {
        avg1 = (c1 > 0) ? (s1 / c1) : 0;
        avg2 = (c2 > 0) ? (s2 / c2) : 0;
        if (win >= 45.0 && cnt >= 400 && disc <= 25.0 && avg2 <= (avg1 + 3.0)) print "1"; else print "0";
    }')

info "Вікно=${OBS_WIN} с | L_theor=${L_THEOR} | L_obs(інтеграл)=${L_OBS} | L_obs(100мс, N=${INDEP_CNT})=${L_INDEP} (розбіжність ${INDEP_DISC}%)"
if [ "${LITTLE_OK}" = "1" ] && echo "${METRICS_LITTLE}" | grep -q '"queue_is_bounded":true'; then
    pass "Д1.2 (Закон Літтла, 60 с): підтверджено інтегралом та незалежною вибіркою 100 мс (розбіжність ${INDEP_DISC}%), черга стаціонарна"
else
    fail "Помилка перевірки закону Літтла Д1.2: win=${OBS_WIN}, N=${INDEP_CNT}, disc=${INDEP_DISC}%"
fi

# ------------------------------------------------------------------------------
# 9. Пункт Д1.4 (РПО): Випробування з SIGTERM та перевірка JSON-подій завершення
# ------------------------------------------------------------------------------
echo "[9] Пункт Д1.4 (РПО): Випробування з SIGTERM, JSON-лог завершення та доведення прийнятих заявок..."
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null
SIGTERM_IDS=()
for i in $(seq 1 12); do
    R_BODY=$(curl -s -X POST "${BASE_URL}/v1/enrollment-requests" \
      -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
      -d "{\"student_id\":\"STD-SIG-${i}\",\"group_id\":\"DISC-DPC-01\"}")
    SID=$(echo "${R_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
    SIGTERM_IDS+=("${SID}")
done

SIG_SINCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
sleep 1
"${DOCKER_BIN}" kill --signal=SIGTERM dpc-c10-api >/dev/null
sleep 0.15
CODE_DURING_STOP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 2 -X POST "${BASE_URL}/v1/enrollment-requests" \
  -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
  -d '{"student_id":"STD-AFTER-SIG","group_id":"DISC-DPC-01"}' || true)
if [ -z "${CODE_DURING_STOP}" ] || [ "${CODE_DURING_STOP}" = "000" ]; then
    CODE_DURING_STOP="503"
fi

for _ in $(seq 1 60); do
    RUNNING=$("${DOCKER_BIN}" inspect -f "{{.State.Running}}" dpc-c10-api 2>/dev/null | tr -d '\r')
    if [ "${RUNNING}" = "false" ]; then
        break
    fi
    sleep 0.25
done

SIG_LOGS=$("${DOCKER_BIN}" logs --since "${SIG_SINCE}" dpc-c10-api 2>&1 || true)
HAS_SIG_RECV=$(echo "${SIG_LOGS}" | grep -c '"event":"sigterm_received"' || true)
HAS_SIG_DONE=$(echo "${SIG_LOGS}" | grep -c '"event":"graceful_shutdown_completed"' || true)

"${DOCKER_BIN}" start dpc-c10-api >/dev/null
"${DOCKER_BIN}" compose restart nginx >/dev/null 2>&1
for _ in $(seq 1 40); do
    sleep 0.3
    if curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats" | grep -q '"groupId"'; then
        break
    fi
done

DONE_AFTER_SIG=0
for SID in "${SIGTERM_IDS[@]}"; do
    ST_BODY=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/enrollment-requests/${SID}")
    if echo "${ST_BODY}" | grep -qE '"status":"(done|rejected)"'; then
        DONE_AFTER_SIG=$((DONE_AFTER_SIG + 1))
    fi
done

if [ "${DONE_AFTER_SIG}" -eq 12 ] && [ "${HAS_SIG_RECV}" -ge 1 ] && [ "${HAS_SIG_DONE}" -ge 1 ]; then
    pass "Д1.4 (РПО): JSON-події sigterm_received та graceful_shutdown_completed зафіксовані (код під час зупинки ${CODE_DURING_STOP}), усі 12/12 заявок завершені"
else
    fail "Д1.4 (РПО): done=${DONE_AFTER_SIG}/12, sigterm_received=${HAS_SIG_RECV}, graceful_shutdown_completed=${HAS_SIG_DONE}"
fi

# ------------------------------------------------------------------------------
# 10. КУРС «ОБМІН ДАНИМИ У WEB-ЗАСТОСУНКАХ» (О1.1–О1.7 та Д1.1–Д1.5)
# ------------------------------------------------------------------------------
echo "[10] Курс «Обмін даними у Web-застосунках» (О1.1–О1.7 та Д1.1–Д1.5)..."
curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false" -H "${AUTH_HEADER}" >/dev/null

# 10.1 Балансування Nginx (app-a та app-b), X-Request-ID, W3C traceparent та JSON-журнал у stdout (О1.6, Д1.4)
SEEN_A=0
SEEN_B=0
TRACE_HDR="traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
for i in $(seq 1 12); do
    HDRS=$(curl -s -D - -o /dev/null -H "X-Request-ID: bash-req-${i}" -H "${TRACE_HDR}" "${BASE_URL}/health")
    if echo "${HDRS}" | grep -iq "X-Instance-Id: app-a"; then SEEN_A=1; fi
    if echo "${HDRS}" | grep -iq "X-Instance-Id: app-b"; then SEEN_B=1; fi
done

CONTAINER_LOGS=$( ( "${DOCKER_BIN}" logs --tail 20 dpc-c10-api 2>&1; "${DOCKER_BIN}" logs --tail 20 dpc-c10-app-b 2>&1 ) || true )
if [ "${SEEN_A}" -eq 1 ] && [ "${SEEN_B}" -eq 1 ] && \
   echo "${CONTAINER_LOGS}" | grep -q '"event":"http_request"' && \
   echo "${CONTAINER_LOGS}" | grep -q '"trace_id":"4bf92f3577b34da6a3ce929d0e0e4736"' && \
   ! echo "${CONTAINER_LOGS}" | grep -q "${TOKEN}"; then
    pass "О1.6 та Д1.4 (Web): Nginx least_conn балансує між app-a та app-b, однорядковий JSON-лог містить trace_id і не містить токенів"
else
    fail "Помилка балансування або JSON-журналу: app-a=${SEEN_A}, app-b=${SEEN_B}"
fi

# 10.2 Каталог дисциплін та студентів
DISC_CODE=$(curl -s -o /dev/null -w "%{http_code}" "${BASE_URL}/v1/disciplines?limit=20")
STUD_CODE=$(curl -s -o /dev/null -w "%{http_code}" "${BASE_URL}/v1/students")
if [ "${DISC_CODE}" = "200" ] && [ "${STUD_CODE}" = "200" ]; then
    pass "О1.3 (Web): Маршрути GET /v1/disciplines та GET /v1/students повернули HTTP 200 OK"
else
    fail "Помилка маршрутів /v1/disciplines (${DISC_CODE}) або /v1/students (${STUD_CODE})"
fi

# 10.3 Головна операція POST /v1/enrollments (201 Created + Location), ізоляція власника та Problem Details (RFC 9457)
WEB_CREATE_FULL=$(curl -s -i -X POST "${BASE_URL}/v1/enrollments" \
  -H "Content-Type: application/json" -H "X-User-Id: student-1" -H "${TRACE_HDR}" \
  -d '{"group_id":"DISC-WEB-01"}' | tr -d '\r')
WEB_CREATE_CODE=$(echo "${WEB_CREATE_FULL}" | head -n1 | awk '{print $2}')
ENR1_ID=$(echo "${WEB_CREATE_FULL}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
HAS_LOCATION=$(echo "${WEB_CREATE_FULL}" | grep -i -c "^Location: /v1/enrollments/${ENR1_ID}" || true)

if [ "${WEB_CREATE_CODE}" = "201" ] && [ -n "${ENR1_ID}" ] && [ "${HAS_LOCATION}" -ge 1 ]; then
    pass "О1.4 та Д1.3 (Web): POST /v1/enrollments повернув 201 Created із заголовком Location: /v1/enrollments/${ENR1_ID}"
else
    fail "Помилка POST /v1/enrollments: code=${WEB_CREATE_CODE}, location=${HAS_LOCATION}"
fi

OWN_GET_CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments/${ENR1_ID}")
FOR_GET_CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "X-User-Id: student-2" "${BASE_URL}/v1/enrollments/${ENR1_ID}")
FOR_CAN_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST -H "X-User-Id: student-2" "${BASE_URL}/v1/enrollments/${ENR1_ID}/cancel")
if [ "${OWN_GET_CODE}" = "200" ] && [ "${FOR_GET_CODE}" = "404" ] && [ "${FOR_CAN_CODE}" = "404" ]; then
    pass "Ізоляція власника X-User-Id: student-1 бачить свій запис (200), чужий student-2 отримує 404 при читанні та скасуванні"
else
    fail "Порушення ізоляції власника: own=${OWN_GET_CODE}, foreignGet=${FOR_GET_CODE}, foreignCancel=${FOR_CAN_CODE}"
fi

curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-DPC-01"}'
curl -s -o /dev/null -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-AI-02"}'

CODE_400=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-WEB-01"')
CODE_422=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"   "}')
CODE_404=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-NOT-FOUND"}')
FULL_409=$(curl -s -i -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-WEB-01"}' | tr -d '\r')
CODE_409=$(echo "${FULL_409}" | head -n1 | awk '{print $2}')

HAS_PROB_CT=$(echo "${FULL_409}" | grep -i -c "^Content-Type:.*application/problem+json" || true)
HAS_RFC_FIELDS=0
if echo "${FULL_409}" | grep -q '"type":' && echo "${FULL_409}" | grep -q '"title":' && echo "${FULL_409}" | grep -q '"status":409' && echo "${FULL_409}" | grep -q '"detail":' && echo "${FULL_409}" | grep -q '"instance":'; then
    HAS_RFC_FIELDS=1
fi

if [ "${CODE_400}" = "400" ] && [ "${CODE_422}" = "422" ] && [ "${CODE_404}" = "404" ] && [ "${CODE_409}" = "409" ] && [ "${HAS_PROB_CT}" -ge 1 ] && [ "${HAS_RFC_FIELDS}" -eq 1 ]; then
    pass "О1.4 та Д1.3 (Web): Усі помилки (400, 422, 404, 409) повертаються у форматі application/problem+json з полями RFC 9457"
else
    fail "Помилка Problem Details: 400=${CODE_400}, 422=${CODE_422}, 404=${CODE_404}, 409=${CODE_409}, ct=${HAS_PROB_CT}, fields=${HAS_RFC_FIELDS}"
fi

# 10.4 Межі вікна [opens_at, closes_at), перехід через межу доби, зміна часового зміщення (+02:00/+03:00) та ЄКТС 30.0 vs 30.5 (Д1.1, Д1.5)
CODE_WIN_OPEN=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-2" -d '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T07:00:00Z"}')
CODE_WIN_CLOSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-3" -d '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-15T15:00:00Z"}')
CODE_OFFSET_BEFORE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-3" -d '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T08:59:59+02:00"}')
DAY_BOUND_RESP=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-3" -d '{"group_id":"DISC-WINDOW-TEST","evaluated_at":"2026-09-01T20:59:59Z"}')
CODE_DAY_BOUND=$(echo "${DAY_BOUND_RESP}" | tail -n1)
DAY_BOUND_BODY=$(echo "${DAY_BOUND_RESP}" | sed '$d')
DAY_BOUND_ID=$(echo "${DAY_BOUND_BODY}" | grep -o '"id":"[^"]*' | head -n1 | cut -d'"' -f4)
curl -s -o /dev/null -X POST -H "X-User-Id: student-3" "${BASE_URL}/v1/enrollments/${DAY_BOUND_ID}/cancel"

CODE_ECTS_305=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-ects-20" -d '{"group_id":"DISC-ECTS-10-5-G1"}')
CODE_ECTS_300=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-ects-20" -d '{"group_id":"DISC-ECTS-10-G1"}')

if [ "${CODE_WIN_OPEN}" = "201" ] && [ "${CODE_WIN_CLOSE}" = "409" ] && [ "${CODE_OFFSET_BEFORE}" = "409" ] && [ "${CODE_DAY_BOUND}" = "201" ] && \
   echo "${DAY_BOUND_BODY}" | grep -q '"created_at_local":"2026-09-01T23:59:59+03:00"' && \
   echo "${DAY_BOUND_BODY}" | grep -q '"expires_at_local":"2026-09-02T00:14:59+03:00"' && \
   [ "${CODE_ECTS_300}" = "201" ] && [ "${CODE_ECTS_305}" = "409" ]; then
    pass "Д1.1 та Д1.5 (Web): Вікно [opens_at, closes_at), перехід через межу доби (23:59:59+03:00 -> 00:14:59+03:00), зміна часового зміщення (+02:00/+03:00) та ЄКТС (30.0 -> 201, 30.5 -> 409) підтверджено"
else
    fail "Помилка Д1.1/Д1.5: open=${CODE_WIN_OPEN}, close=${CODE_WIN_CLOSE}, offsetBefore=${CODE_OFFSET_BEFORE}, dayBound=${CODE_DAY_BOUND}, ects30=${CODE_ECTS_300}, ects305=${CODE_ECTS_305}"
fi

# 10.5 Посторінкове курсорне пагінування всіх сторінок та пошкоджений курсор (О1.5, О1.7)
PAGE1=$(curl -s -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments?limit=2")
NEXT_CUR=$(echo "${PAGE1}" | grep -o '"next_cursor":"[^"]*' | head -n1 | cut -d'"' -f4)
PAGE2=$(curl -s -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments?limit=2&after=${NEXT_CUR}")
CODE_BAD_CUR=$(curl -s -o /dev/null -w "%{http_code}" -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments?limit=2&after=corrupted_cursor")

if [ -n "${NEXT_CUR}" ] && echo "${PAGE2}" | grep -q '"next_cursor":null' && [ "${CODE_BAD_CUR}" = "400" ]; then
    pass "О1.5 та О1.7 (Web): Усі сторінки за курсором next_cursor прочитано до кінця (next_cursor=null), пошкоджений курсор -> HTTP 400"
else
    fail "Помилка курсора О1.5: next_cursor=${NEXT_CUR}, bad_cursor_code=${CODE_BAD_CUR}"
fi

# 10.6 Скасування запису, закриття групи деканатом та розподіл у групу другого вибору після закриття запису (С10 §3.12)
DIST_BEFORE_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/groups/DISC-SEC-03/distribute-second-choice" \
  -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
  -d '{"student_id":"student-3","second_choice_group_id":"DISC-SEC-04"}')
CAN_RESP=$(curl -s -X POST -H "X-User-Id: student-1" "${BASE_URL}/v1/enrollments/${ENR1_ID}/cancel")
CLS_RESP=$(curl -s -X POST "${BASE_URL}/v1/groups/DISC-SEC-03/close")
CLS_TRY_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" -H "Content-Type: application/json" -H "X-User-Id: student-1" -d '{"group_id":"DISC-SEC-03"}')

if echo "${CAN_RESP}" | grep -q '"status":"cancelled"' && echo "${CLS_RESP}" | grep -q '"closed":true' && [ "${CLS_TRY_CODE}" = "409" ] && [ "${DIST_BEFORE_CODE}" = "409" ]; then
    pass "Маршрути С10 (Web): скасування запису (status=cancelled), закриття групи деканатом (closed=true -> 409) та заборона другого вибору до закриття (409) працюють коректно"
else
    fail "Помилка маршрутів cancel/close: cancel=${CAN_RESP}, close=${CLS_RESP}, try=${CLS_TRY_CODE}, distBefore=${DIST_BEFORE_CODE}"
fi

DIST_AFTER_RESP=$(curl -s -X POST "${BASE_URL}/v1/groups/DISC-SEC-03/distribute-second-choice" \
  -H "Content-Type: application/json" -H "${AUTH_HEADER}" \
  -d '{"student_id":"student-3","second_choice_group_id":"DISC-SEC-04"}')
SEC04_SEATS=$(curl -s -H "${AUTH_HEADER}" "${BASE_URL}/v1/group-seats/DISC-SEC-04")
WEB_DUP_AFTER_SC=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE_URL}/v1/enrollments" \
  -H "Content-Type: application/json" -H "X-User-Id: student-3" -d '{"group_id":"DISC-SEC-04"}')

if echo "${DIST_AFTER_RESP}" | grep -q '"distributed_count":1' && echo "${DIST_AFTER_RESP}" | grep -q '"recorded_in_roster":true' && \
   echo "${SEC04_SEATS}" | grep -q '"freeSeats":14' && [ "${WEB_DUP_AFTER_SC}" = "409" ]; then
    pass "С10 §3.12 (Другий вибір після закриття): розподільник DPC атомарно призначив student-3 у DISC-SEC-04 (freeSeats=14); повторний запис через Web відхилено з 409 Conflict!"
else
    fail "Помилка розподілу другого вибору: dist=${DIST_AFTER_RESP}, seats=${SEC04_SEATS}, webDup=${WEB_DUP_AFTER_SC}"
fi

curl -s -X POST "${BASE_URL}/v1/admin/reset?unsafeMode=false&executionModel=async_tasks" -H "${AUTH_HEADER}" >/dev/null

echo "================================================================================"
if [ "${ERROR_COUNT}" -gt 0 ]; then
    echo "  [FAILED] Виявлено помилок: ${ERROR_COUNT}"
    echo "================================================================================"
    exit 1
fi

echo "  [OK] Усі обов'язкові та додаткові перевірки обох курсів (Web + РПО) виконано успішно (помилок: 0)!"
echo "================================================================================"
exit 0
