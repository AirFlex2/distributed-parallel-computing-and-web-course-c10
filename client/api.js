// Клієнтський модуль взаємодії з API сервісу запису на дисципліни (Тема С10: Web + РПО, ЛР1)
// Написаний іншою мовою (JavaScript ES6+), ніж сервер (C# / ASP.NET Core 8.0) згідно з Д1.2 (Web).

function getApiBase() {
    return document.getElementById('apiUrl').value.replace(/\/+$/, '');
}

function getSelectedStudentId() {
    const el = document.getElementById('webUserSelect');
    return el ? el.value : 'student-1';
}

function getAuthHeaders(customRequestId = null) {
    const token = document.getElementById('authToken').value.trim();
    const traceparent = document.getElementById('traceparentInput')?.value?.trim();
    const reqId = customRequestId || `web-req-${Date.now()}-${Math.floor(Math.random() * 10000)}`;
    const headers = {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${token}`,
        'X-User-Id': getSelectedStudentId(),
        'X-Request-ID': reqId
    };
    if (traceparent) {
        headers['traceparent'] = traceparent;
    }
    return headers;
}

function appendLog(message) {
    const box = document.getElementById('experimentLog');
    const time = new Date().toLocaleTimeString('uk-UA', { hour12: false });
    box.textContent = `[${time}] ${message}\n` + box.textContent;
}

function appendWebOpLog(message) {
    const box = document.getElementById('webMainOpResult');
    const time = new Date().toLocaleTimeString('uk-UA', { hour12: false });
    box.textContent = `[${time}] ${message}\n` + box.textContent;
}

function appendWebCursorLog(message) {
    const box = document.getElementById('webCursorLog');
    const time = new Date().toLocaleTimeString('uk-UA', { hour12: false });
    box.textContent = `[${time}] ${message}\n` + box.textContent;
}

// ============================================================================
// ЧАСТИНА А: ФУНКЦІЇ КЛІЄНТА КУРСУ «ОБМІН ДАНИМИ У WEB-ЗАСТОСУНКАХ» (О1.7, Д1.2, Д1.5)
// ============================================================================

// Проєкція відомої моделі запису студента з ігноруванням невідомих зайвих полів сервера (О1.7)
function parseWebEnrollmentDtoSafely(raw) {
    return {
        id: String(raw.id),
        student_id: String(raw.student_id),
        group_id: String(raw.group_id),
        discipline_id: String(raw.discipline_id),
        discipline_title: String(raw.discipline_title || ''),
        ects: Number(raw.ects),
        status: String(raw.status),
        version: Number(raw.version),
        created_at: String(raw.created_at),
        expires_at: String(raw.expires_at),
        created_at_local: String(raw.created_at_local || raw.created_at),
        university_tz: String(raw.university_tz || 'Europe/Kyiv'),
        instance_id: String(raw.instance_id || 'unknown')
    };
}

// Завантаження каталогу дисциплін та груп через балансувальник Nginx (GET /v1/disciplines)
async function loadWebDisciplines() {
    try {
        const res = await fetch(`${getApiBase()}/v1/disciplines?limit=20`, {
            headers: getAuthHeaders()
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        const tbody = document.getElementById('webDisciplinesTableBody');
        tbody.innerHTML = '';

        for (const d of (data.items || [])) {
            for (const g of (d.groups || [])) {
                const tr = document.createElement('tr');
                const statusBadge = g.closed
                    ? '<span class="badge badge-err">Закрито</span>'
                    : (g.window_active
                        ? '<span class="badge badge-ok">Відкрито</span>'
                        : '<span class="badge badge-warn">Поза вікном</span>');

                tr.innerHTML = `
                    <td><strong>${g.id}</strong></td>
                    <td>${d.title} <span style="color:#64748b">(${d.code})</span></td>
                    <td><strong>${Number(d.ects).toFixed(1)}</strong></td>
                    <td><strong>${g.available}</strong> / ${g.seats}</td>
                    <td>
                        <div style="font-size:11px">${g.opens_at_local} &rarr; ${g.closes_at_local}</div>
                        ${statusBadge}
                    </td>
                    <td>
                        <button class="teal" style="padding:4px 8px;font-size:12px" onclick="createWebEnrollment('${g.id}')">Записатися</button>
                        <button class="secondary" style="padding:4px 8px;font-size:12px" onclick="closeWebGroup('${g.id}')">Закрити</button>
                    </td>
                `;
                tbody.appendChild(tr);
            }
        }
    } catch (err) {
        appendWebOpLog(`Помилка завантаження дисциплін: ${err.message}`);
    }
}

// Головна операція (О1.4, О1.7): POST /v1/enrollments із відображенням 201 Created або Problem Details
async function createWebEnrollment(overrideGroupId = null, evaluatedAt = null, overrideStudentId = null) {
    const groupId = overrideGroupId || document.getElementById('webGroupIdSelect').value;
    const headers = getAuthHeaders();
    if (overrideStudentId) {
        headers['X-User-Id'] = overrideStudentId;
    }

    const payload = { group_id: groupId };
    if (evaluatedAt) {
        payload.evaluated_at = evaluatedAt;
    }

    try {
        const res = await fetch(`${getApiBase()}/v1/enrollments`, {
            method: 'POST',
            headers,
            body: JSON.stringify(payload)
        });

        const reqIdHeader = res.headers.get('X-Request-ID') || '—';
        const instanceHeader = res.headers.get('X-Instance-Id') || '—';
        const locationHeader = res.headers.get('Location') || '—';
        const rawBody = await res.json().catch(() => ({}));

        if (res.status === 201) {
            const safeModel = parseWebEnrollmentDtoSafely(rawBody);
            appendWebOpLog(
                `[201 Created] Запис створено успішно (після COMMIT у PostgreSQL)!\n` +
                `  Location: ${locationHeader} | Вузол за Nginx: ${instanceHeader} | X-Request-ID: ${reqIdHeader}\n` +
                `  id="${safeModel.id}", student_id="${safeModel.student_id}", group_id="${safeModel.group_id}", ` +
                `ects=${safeModel.ects.toFixed(1)}, status="${safeModel.status}", created_at_local="${safeModel.created_at_local}"\n` +
                `  (Перевірка О1.7: зайве поле сервера extra_server_field="${rawBody.extra_server_field}" безпечно проігноровано клієнтом).`
            );
        } else {
            appendWebOpLog(
                `[HTTP ${res.status} Problem Details] (${res.headers.get('Content-Type')})\n` +
                `  Вузол: ${instanceHeader} | X-Request-ID: ${reqIdHeader}\n` +
                `  type: ${rawBody.type}\n` +
                `  title: ${rawBody.title}\n` +
                `  status: ${rawBody.status}\n` +
                `  detail: ${rawBody.detail}\n` +
                `  error_code: ${rawBody.error_code || '—'}`
            );
        }

        await refreshAll();
        return { status: res.status, body: rawBody, headers: res.headers };
    } catch (err) {
        appendWebOpLog(`Мережева помилка POST /v1/enrollments: ${err.message}`);
        return null;
    }
}

// Скасування запису студентом: POST /v1/enrollments/{id}/cancel
async function cancelWebEnrollment(enrollmentId) {
    try {
        const res = await fetch(`${getApiBase()}/v1/enrollments/${enrollmentId}/cancel`, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify({ reason: 'Скасовано студентом через веб-клієнт' })
        });
        const body = await res.json().catch(() => ({}));
        if (res.ok) {
            appendWebOpLog(`[200 OK] Запис ${enrollmentId} скасовано (status="${body.status}", місце в групі ${body.group_id} повернуто).`);
        } else {
            appendWebOpLog(`[HTTP ${res.status}] Помилка скасування: ${body.detail || body.title}`);
        }
        await refreshAll();
    } catch (err) {
        appendWebOpLog(`Помилка скасування запису: ${err.message}`);
    }
}

// Закриття групи працівником деканату: POST /v1/groups/{id}/close
async function closeWebGroup(groupId) {
    try {
        const res = await fetch(`${getApiBase()}/v1/groups/${groupId}/close`, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify({ reason: 'Закрито деканатом' })
        });
        const body = await res.json().catch(() => ({}));
        if (res.ok) {
            appendWebOpLog(`[200 OK] Групу ${groupId} закрито працівником деканату (closed=${body.closed}).`);
        } else {
            appendWebOpLog(`[HTTP ${res.status}] Помилка закриття групи: ${body.detail || body.title}`);
        }
        await refreshAll();
    } catch (err) {
        appendWebOpLog(`Помилка закриття групи: ${err.message}`);
    }
}

// Посторінкове читання ВСІХ сторінок списку з курсором next_cursor до кінця (О1.5, О1.7)
async function readAllWebEnrollmentPages() {
    const limit = parseInt(document.getElementById('webCursorLimit').value, 10) || 2;
    const studentId = getSelectedStudentId();
    const tbody = document.getElementById('webEnrollmentsTableBody');
    tbody.innerHTML = '';

    let cursor = null;
    let pageNum = 0;
    const allItems = [];
    let latestMeta = null;

    appendWebCursorLog(`Старт посторінкового обходу GET /v1/enrollments для ${studentId} з limit=${limit}...`);

    while (true) {
        pageNum++;
        const url = new URL(`${getApiBase()}/v1/enrollments`);
        url.searchParams.set('limit', String(limit));
        if (cursor) {
            url.searchParams.set('after', cursor);
        }

        const res = await fetch(url.toString(), { headers: getAuthHeaders() });
        const body = await res.json().catch(() => ({}));

        if (!res.ok) {
            appendWebCursorLog(`[Сторінка ${pageNum}] Помилка HTTP ${res.status}: ${body.detail || body.title}`);
            return;
        }

        latestMeta = body;
        const pageItems = (body.items || []).map(parseWebEnrollmentDtoSafely);
        allItems.push(...pageItems);

        appendWebCursorLog(
            `[Сторінка ${pageNum}] Отримано записів: ${pageItems.length} | ` +
            `next_cursor: ${body.next_cursor ? body.next_cursor.slice(0, 24) + '...' : 'null (кінець списку)'}`
        );

        if (!body.next_cursor) {
            break;
        }
        cursor = body.next_cursor;
        if (pageNum > 50) break;
    }

    if (latestMeta) {
        const badge = document.getElementById('webStudentStatusBadge');
        badge.textContent = `Студент: ${latestMeta.student_id} | Активних дисциплін: ${latestMeta.active_count}/${latestMeta.max_disciplines} | Кредитів ЄКТС: ${Number(latestMeta.active_ects).toFixed(1)}/${Number(latestMeta.max_ects).toFixed(1)}`;
    }

    for (const item of allItems) {
        const tr = document.createElement('tr');
        const stBadge = item.status === 'held' || item.status === 'confirmed'
            ? `<span class="badge badge-ok">${item.status} (v${item.version})</span>`
            : `<span class="badge badge-warn">${item.status} (v${item.version})</span>`;

        tr.innerHTML = `
            <td style="font-family:monospace;font-size:11px">${item.id}</td>
            <td><strong>${item.group_id}</strong><div style="font-size:11px;color:#64748b">${item.discipline_title}</div></td>
            <td>${item.ects.toFixed(1)}</td>
            <td>${stBadge}</td>
            <td style="font-size:11px">${item.created_at_local}</td>
            <td><span class="badge badge-ok">${item.instance_id}</span></td>
            <td>
                ${(item.status === 'held' || item.status === 'confirmed')
                    ? `<button class="danger" style="padding:3px 7px;font-size:11px" onclick="cancelWebEnrollment('${item.id}')">Скасувати</button>`
                    : '—'}
            </td>
        `;
        tbody.appendChild(tr);
    }

    appendWebCursorLog(`Обхід завершено: всього прочитано сторінок = ${pageNum}, всього записів = ${allItems.length}.`);
}

// Перевірка відхилення пошкодженого курсора з кодом 400 Bad Request у форматі Problem Details (О1.5)
async function testCorruptedCursor() {
    const res = await fetch(`${getApiBase()}/v1/enrollments?limit=2&after=corrupted_cursor_!@#`, {
        headers: getAuthHeaders()
    });
    const body = await res.json().catch(() => ({}));
    appendWebCursorLog(
        `Перевірка пошкодженого курсора -> HTTP ${res.status} (${res.headers.get('Content-Type')}): ` +
        `title="${body.title}", detail="${body.detail}", error_code="${body.error_code}"`
    );
}

// Автотест граничних правил Д1.1 та Д1.5:
// 1. Часове вікно [opens_at, closes_at): запит у момент opens_at -> 201, у момент closes_at -> 409
//    (якщо ALLOW_TEST_OVERRIDES=false, сервер захищено від підміни часу та повертає 403 Forbidden)
// 2. Перехід через межу доби (2026-09-01T21:00:00Z -> 2026-09-02T00:00:00+03:00 у Києві) та зміна часового зміщення (+02:00 / +03:00)
// 3. Ліміт ЄКТС: сума 30.0 -> 201, сума 30.5 -> 409
async function runWebBoundaryTests() {
    appendWebOpLog(`=== Старт автотесту граничних умов Д1.1 та Д1.5 ===`);
    const rClose = await createWebEnrollment('DISC-WINDOW-TEST', '2026-09-15T15:00:00Z', 'student-2');
    if (rClose && rClose.status === 403) {
        appendWebOpLog(
            `[Захист активний] ALLOW_TEST_OVERRIDES=false: поле evaluated_at заблоковано (403 Forbidden). ` +
            `Перевіряємо вікно за реальним часом сервера (поза [2026-09-01, 2026-09-15) -> 409 Conflict)...`
        );
        await createWebEnrollment('DISC-WINDOW-TEST', null, 'student-2');
    } else {
        await createWebEnrollment('DISC-WINDOW-TEST', '2026-09-01T07:00:00Z', 'student-2');
        await createWebEnrollment('DISC-WINDOW-TEST', '2026-09-01T08:59:59+02:00', 'student-3');
        const dayBoundary = await createWebEnrollment('DISC-WINDOW-TEST', '2026-09-01T21:00:00Z', 'student-3');
        if (dayBoundary && dayBoundary.status === 201 && dayBoundary.body?.id) {
            await cancelWebEnrollment(dayBoundary.body.id);
        }
    }
    await createWebEnrollment('DISC-ECTS-10-5-G1', null, 'student-ects-20');
    await createWebEnrollment('DISC-ECTS-10-G1', null, 'student-ects-20');
}

// Автотест усіх кодів помилок Problem Details (400, 404, 409, 422) + захисту X-User-Id (О1.4, Д1.3)
async function runWebProblemDetailsTests() {
    appendWebOpLog(`=== Старт автотесту Problem Details (400 / 422 / 404 / 409) ===`);

    // 1. 400 Bad Request (відсутній обов'язковий заголовок X-User-Id)
    const headersWithoutUser = getAuthHeaders();
    delete headersWithoutUser['X-User-Id'];
    const resNoUser = await fetch(`${getApiBase()}/v1/enrollments`, {
        method: 'POST',
        headers: headersWithoutUser,
        body: JSON.stringify({ group_id: 'DISC-WEB-01' })
    });
    const bNoUser = await resNoUser.json().catch(() => ({}));
    appendWebOpLog(`[Тест 400 без X-User-Id] Статус=${resNoUser.status}, error_code="${bNoUser.error_code}", detail="${bNoUser.detail}"`);

    // 2. 400 Bad Request (синтаксично некоректний JSON)
    const res400 = await fetch(`${getApiBase()}/v1/enrollments`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: '{"group_id": "DISC-WEB-01"'
    });
    const b400 = await res400.json().catch(() => ({}));
    appendWebOpLog(`[Тест 400 битий JSON] Статус=${res400.status}, title="${b400.title}", error_code="${b400.error_code}"`);

    // 3. 422 Unprocessable Entity (спроба передати student_id у тілі запиту — заборонено)
    const resOwnerBody = await fetch(`${getApiBase()}/v1/enrollments`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: JSON.stringify({ group_id: 'DISC-WEB-01', student_id: 'student-2' })
    });
    const bOwnerBody = await resOwnerBody.json().catch(() => ({}));
    appendWebOpLog(`[Тест 422 student_id у тілі] Статус=${resOwnerBody.status}, error_code="${bOwnerBody.error_code}"`);

    // 4. 422 Unprocessable Entity (порожній group_id)
    await createWebEnrollment('   ');

    // 5. 404 Not Found (неіснуюча група)
    await createWebEnrollment('DISC-DOES-NOT-EXIST-999');

    // 6. 409 Conflict (перевищення ліміту K дисциплін у student-limit-max)
    await createWebEnrollment('DISC-DB-01', null, 'student-limit-max');
}

// ============================================================================
// ЧАСТИНА Б: ФУНКЦІЇ КЛІЄНТА КУРСУ «РОЗПОДІЛЕНІ ТА ПАРАЛЕЛЬНІ ОБЧИСЛЕННЯ» (О1.1–О1.5, Д1.1–Д1.4)
// ============================================================================

async function loadGroups() {
    try {
        const res = await fetch(`${getApiBase()}/v1/group-seats`, { headers: getAuthHeaders() });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const groups = await res.json();
        const tbody = document.getElementById('groupsTableBody');
        tbody.innerHTML = '';

        for (const g of groups) {
            const tr = document.createElement('tr');
            const badgeClass = g.invariantHolds ? 'badge-ok' : 'badge-err';
            const badgeText = g.invariantHolds
                ? `Дотримано (${g.taken} <= ${g.seats})`
                : `ПОРУШЕНО (${g.taken} > ${g.seats})`;

            tr.innerHTML = `
                <td><strong>${g.groupId}</strong></td>
                <td>${g.disciplineTitle}</td>
                <td>${g.seats}</td>
                <td><strong>${g.taken}</strong></td>
                <td><span class="badge ${badgeClass}">${badgeText}</span></td>
            `;
            tbody.appendChild(tr);
        }
    } catch (err) {
        appendLog(`Помилка завантаження груп: ${err.message}`);
    }
}

async function loadMetrics() {
    try {
        const [res, healthRes] = await Promise.all([
            fetch(`${getApiBase()}/metrics?format=json`),
            fetch(`${getApiBase()}/health`)
        ]);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        const healthData = healthRes.ok ? await healthRes.json().catch(() => ({})) : {};

        const overridesBadge = document.getElementById('testOverridesBadge');
        if (overridesBadge) {
            const enabled = Boolean(healthData.allow_test_overrides ?? data.service_state?.allow_test_overrides);
            overridesBadge.className = enabled ? 'badge badge-warn' : 'badge badge-ok';
            overridesBadge.textContent = enabled
                ? 'ALLOW_TEST_OVERRIDES=true (Тестовий стенд)'
                : 'ALLOW_TEST_OVERRIDES=false (Робочий захищений режим)';
        }

        const summary = document.getElementById('metricsSummary');
        const modeBadge = data.service_state.unsafe_mode
            ? '<span class="badge badge-err">UNSAFE (Без блокувань)</span>'
            : '<span class="badge badge-ok">SAFE (Транзакційний)</span>';

        summary.innerHTML = `
            <div class="stat-box">
                <div class="stat-label">У черзі / В обробці</div>
                <div class="stat-val">${data.queue_length} / ${data.in_processing}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Успішно (done) / Відмов (rejected)</div>
                <div class="stat-val">${data.counters.total_done} / ${data.counters.total_rejected}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Відмов черги (HTTP 429)</div>
                <div class="stat-val">${data.counters.total_429_too_many_requests}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Очікування в черзі p95</div>
                <div class="stat-val">${data.intervals_ms.queue_wait_ms.p95_ms} мс</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Літтл: L_теор / L_інт / L_100мс</div>
                <div class="stat-val" style="font-size: 14px;">${data.littles_law.l_theoretical_avg_queue_length} / ${data.littles_law.l_observed_avg_queue_length} / ${data.littles_law.l_observed_independent_sampled ?? '—'}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Режим захисту О1.4</div>
                <div class="stat-val" style="font-size: 13px;">${modeBadge}</div>
            </div>
        `;

        document.getElementById('metricsRaw').textContent = JSON.stringify(data, null, 2);
    } catch (err) {
        appendLog(`Помилка отримання метрик: ${err.message}`);
    }
}

async function refreshAll() {
    await Promise.all([
        loadWebDisciplines(),
        readAllWebEnrollmentPages(),
        loadGroups(),
        loadMetrics()
    ]);
}

async function submitSingleEnrollment() {
    const studentId = document.getElementById('studentIdInput').value.trim();
    const groupId = document.getElementById('groupIdSelect').value;
    const resultBox = document.getElementById('singleRequestResult');

    try {
        resultBox.innerHTML = 'Надсилання запиту...';
        const res = await fetch(`${getApiBase()}/v1/enrollment-requests`, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify({ studentId, groupId })
        });

        const body = await res.json();
        if (res.status !== 202) {
            resultBox.innerHTML = `<span class="badge badge-err">Помилка HTTP ${res.status}</span> ${body.detail || body.title}`;
            await refreshAll();
            return;
        }

        const reqId = body.id;
        appendLog(`Прийнято заявку ${reqId} (202 Accepted, статус: ${body.status}). Очікування обробки воркером...`);

        const pollTimer = setInterval(async () => {
            const pollRes = await fetch(`${getApiBase()}/v1/enrollment-requests/${reqId}`, {
                headers: getAuthHeaders()
            });
            if (!pollRes.ok) return;
            const statusData = await pollRes.json();

            resultBox.innerHTML = `
                <div><strong>ID заявки:</strong> ${statusData.id}</div>
                <div><strong>Статус:</strong> <span class="badge badge-warn">${statusData.status}</span></div>
                ${statusData.rejectionReason ? `<div><strong>Причина:</strong> ${statusData.rejectionReason}</div>` : ''}
                <div><strong>Очікування в черзі:</strong> ${statusData.queueWaitMs ?? '—'} мс |
                     <strong>Обробка:</strong> ${statusData.processingTimeMs ?? '—'} мс |
                     <strong>Очікування видачі (GET):</strong> ${statusData.deliveryWaitMs ?? '—'} мс</div>
            `;

            if (['done', 'rejected', 'failed'].includes(statusData.status)) {
                clearInterval(pollTimer);
                appendLog(`Заявка ${reqId} завершилась у стані '${statusData.status}' (first_read_at зафіксовано).`);
                await refreshAll();
            }
        }, 250);
    } catch (err) {
        resultBox.innerHTML = `<span class="badge badge-err">Помилка мережі: ${err.message}</span>`;
    }
}

async function runStressBatch() {
    const groupId = document.getElementById('stressGroup').value;
    const count = parseInt(document.getElementById('stressCount').value, 10) || 100;

    appendLog(`Старт пакетного навантаження: ${count} одночасних запитів у групу ${groupId}...`);

    const requests = [];
    for (let i = 1; i <= count; i++) {
        const studentId = `STRESS-STD-${Date.now()}-${i}`;
        requests.push(
            fetch(`${getApiBase()}/v1/enrollment-requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify({ studentId, groupId })
            }).then(async r => ({ status: r.status, retryAfter: r.headers.get('Retry-After'), body: await r.json().catch(() => ({})) }))
        );
    }

    const healthLatencies = [];
    for (let h = 0; h < 20; h++) {
        const t0 = performance.now();
        await fetch(`${getApiBase()}/health`);
        healthLatencies.push(performance.now() - t0);
    }
    healthLatencies.sort((a, b) => a - b);
    const healthP95 = healthLatencies[Math.floor(healthLatencies.length * 0.95) - 1] || healthLatencies[0];

    const results = await Promise.all(requests);
    const code202 = results.filter(r => r.status === 202);
    const code429 = results.filter(r => r.status === 429);
    const code503 = results.filter(r => r.status === 503);

    appendLog(`Результат прийому пакета (${count} запитів): 202 Accepted = ${code202.length}, 429 Too Many Requests = ${code429.length}, 503 = ${code503.length}.`);
    appendLog(`Виміряний p95 маршруту GET /health під час пікового навантаження (О1.2): ${healthP95.toFixed(2)} мс (норма <= 100 мс).`);

    await refreshAll();

    setTimeout(async () => {
        for (const item of code202.slice(0, 30)) {
            if (item.body && item.body.id) {
                await fetch(`${getApiBase()}/v1/enrollment-requests/${item.body.id}`, { headers: getAuthHeaders() });
            }
        }
        await refreshAll();
        appendLog(`Фонова обробка пакета завершена. Перевірте таблицю груп (О1.4) та метрики (О1.5).`);
    }, 3500);
}

// Дослід перевірки закону Літтла (Д1.2):
// стабільний потік ~4 запити/с протягом 60 секунд із виключенням перших 10 секунд розігріву
async function runLittlesLawExperiment() {
    const groupId = 'DISC-DPC-01';
    const totalSeconds = 60;
    const warmupSeconds = 10;
    const requestsPerSecond = 4;

    appendLog(
        `Старт досліду закону Літтла (Д1.2): ~${requestsPerSecond} зап./с протягом ${totalSeconds} с ` +
        `(перші ${warmupSeconds} с розігріву виключаються з вимірювань)...`
    );

    const resetRes = await fetch(`${getApiBase()}/v1/admin/reset?unsafeMode=false`, {
        method: 'POST',
        headers: getAuthHeaders()
    });
    if (resetRes.status === 403) {
        appendLog(`[Увага] Скидання стенду вимкнено (ALLOW_TEST_OVERRIDES=false). Дослід Д1.2 виконується без скидання БД...`);
    }

    const createdIds = [];
    const postWarmupQueueLengths = [];
    let reqCounter = 0;

    for (let sec = 0; sec < totalSeconds; sec++) {
        const tickStart = performance.now();

        if (sec === warmupSeconds) {
            await fetch(`${getApiBase()}/v1/admin/reset?metricsOnly=true`, {
                method: 'POST',
                headers: getAuthHeaders()
            });
            appendLog(`Розігрів (${warmupSeconds} с) завершено. Розпочато залікове вікно вимірювань (${totalSeconds - warmupSeconds} с)...`);
        }

        for (let b = 0; b < requestsPerSecond; b++) {
            reqCounter++;
            fetch(`${getApiBase()}/v1/enrollment-requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify({ studentId: `LITTLE-STD-${reqCounter}`, groupId })
            })
                .then(r => r.json())
                .then(d => { if (d && d.id) createdIds.push(d.id); })
                .catch(() => {});
        }

        if (sec >= warmupSeconds) {
            await new Promise(r => setTimeout(r, 120));
            try {
                const hRes = await fetch(`${getApiBase()}/health`);
                if (hRes.ok) {
                    const hData = await hRes.json();
                    postWarmupQueueLengths.push(hData.queue_length);
                }
            } catch (_) {}
        }

        if ((sec + 1) % 10 === 0) {
            await refreshAll();
            appendLog(`Прогрес досліду Д1.2: ${sec + 1}/${totalSeconds} с виконано...`);
        }

        const elapsedTick = performance.now() - tickStart;
        if (elapsedTick < 1000) {
            await new Promise(r => setTimeout(r, 1000 - elapsedTick));
        }
    }

    const metricsRes = await fetch(`${getApiBase()}/metrics?format=json`);
    const metricsData = await metricsRes.json();
    const ll = metricsData.littles_law;

    const mid = Math.floor(postWarmupQueueLengths.length / 2) || 1;
    const firstHalfAvg = postWarmupQueueLengths.slice(0, mid).reduce((a, b) => a + b, 0) / mid;
    const secondHalfAvg = postWarmupQueueLengths.slice(mid).reduce((a, b) => a + b, 0) / Math.max(1, postWarmupQueueLengths.length - mid);
    const clientHttpSampleAvg = postWarmupQueueLengths.length > 0
        ? postWarmupQueueLengths.reduce((a, b) => a + b, 0) / postWarmupQueueLengths.length
        : 0;
    const maxObserved = postWarmupQueueLengths.length > 0 ? Math.max(...postWarmupQueueLengths) : 0;
    const isStable = ll.queue_is_bounded && (secondHalfAvg <= firstHalfAvg + 3.0);

    appendLog(
        `Перевірка стаціонарності черги (50 с після розігріву): ` +
        `середня в 1-й половині = ${firstHalfAvg.toFixed(2)}, у 2-й половині = ${secondHalfAvg.toFixed(2)}, ` +
        `клієнтська HTTP-вибірка = ${clientHttpSampleAvg.toFixed(4)}, макс. = ${maxObserved}. ` +
        `Черга не зростає необмежено: ${isStable ? 'ТАК (стабільна)' : 'НІ'}.`
    );
    appendLog(
        `Результати закону Літтла (Д1.2): вікно = ${ll.observation_window_sec} с, ` +
        `lambda = ${ll.lambda_arrival_rate_rps} зап./с, W = ${ll.w_avg_queue_wait_sec} с, ` +
        `L_теор = ${ll.l_theoretical_avg_queue_length}, ` +
        `L_спост (точний інтеграл) = ${ll.l_observed_avg_queue_length} (розбіжність ${ll.relative_discrepancy_pct}%), ` +
        `L_спост (незалежна вибірка 100 мс, N=${ll.independent_sample_count}) = ${ll.l_observed_independent_sampled} ` +
        `(розбіжність ${ll.independent_discrepancy_pct}%).`
    );

    await new Promise(r => setTimeout(r, 1500));
    for (const id of createdIds.slice(-40)) {
        await fetch(`${getApiBase()}/v1/enrollment-requests/${id}`, { headers: getAuthHeaders() });
    }
    await refreshAll();
}

async function resetEnvironment(unsafeMode) {
    try {
        const res = await fetch(`${getApiBase()}/v1/admin/reset?unsafeMode=${unsafeMode}`, {
            method: 'POST',
            headers: getAuthHeaders()
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            const msg = `[HTTP ${res.status}] ${data.detail || data.title || 'Операція заборонена у робочому режимі (увімкніть ALLOW_TEST_OVERRIDES=true).'}`;
            appendLog(msg);
            appendWebOpLog(msg);
            return;
        }
        appendLog(`${data.message} Поточний UNSAFE_MODE = ${data.unsafe_mode}.`);
        appendWebOpLog(`Стенд скинуто до початкових значень.`);
        await refreshAll();
    } catch (err) {
        appendLog(`Помилка скидання стенду: ${err.message}`);
    }
}

window.addEventListener('DOMContentLoaded', () => {
    refreshAll();
    setInterval(() => {
        loadGroups();
        loadMetrics();
    }, 3000);
});
