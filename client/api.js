// Клієнтський модуль взаємодії з API сервісу запису на дисципліни (Тема С10: Web + РПО, ЛР1)
// Написаний іншою мовою (JavaScript ES6+), ніж сервер (C# / ASP.NET Core 8.0) згідно з Д1.2 (Web).

function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

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

// Завантаження каталогу дисциплін та груп через балансувальник Nginx (GET /v1/disciplines) з повним обходом усіх сторінок (О1.5, О1.7)
async function loadWebDisciplines() {
    try {
        let cursor = null;
        const allDisciplines = [];
        const seenCursors = new Set();

        while (true) {
            const url = new URL(`${getApiBase()}/v1/disciplines`);
            url.searchParams.set('limit', '20');
            if (cursor) {
                url.searchParams.set('after', cursor);
            }

            const res = await fetch(url.toString(), {
                headers: getAuthHeaders()
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();

            if (Array.isArray(data.items)) {
                allDisciplines.push(...data.items);
            }

            if (!data.next_cursor || seenCursors.has(data.next_cursor)) {
                break;
            }
            seenCursors.add(data.next_cursor);
            cursor = data.next_cursor;
        }

        const tbody = document.getElementById('webDisciplinesTableBody');
        tbody.innerHTML = '';

        for (const d of allDisciplines) {
            for (const g of (d.groups || [])) {
                const tr = document.createElement('tr');

                const tdId = document.createElement('td');
                const strongId = document.createElement('strong');
                strongId.textContent = g.id;
                tdId.appendChild(strongId);
                tr.appendChild(tdId);

                const tdTitle = document.createElement('td');
                tdTitle.textContent = (d.title || '') + ' ';
                const spanCode = document.createElement('span');
                spanCode.style.color = '#64748b';
                spanCode.textContent = `(${d.code || ''})`;
                tdTitle.appendChild(spanCode);
                tr.appendChild(tdTitle);

                const tdEcts = document.createElement('td');
                const strongEcts = document.createElement('strong');
                strongEcts.textContent = Number(d.ects || 0).toFixed(1);
                tdEcts.appendChild(strongEcts);
                tr.appendChild(tdEcts);

                const tdSeats = document.createElement('td');
                const strongAvail = document.createElement('strong');
                strongAvail.textContent = String(g.available);
                tdSeats.appendChild(strongAvail);
                tdSeats.appendChild(document.createTextNode(` / ${g.seats}`));
                tr.appendChild(tdSeats);

                const tdWindow = document.createElement('td');
                const divWin = document.createElement('div');
                divWin.style.fontSize = '11px';
                divWin.textContent = `${g.opens_at_local} \u2192 ${g.closes_at_local}`;
                const spanBadge = document.createElement('span');
                if (g.closed) {
                    spanBadge.className = 'badge badge-err';
                    spanBadge.textContent = 'Закрито';
                } else if (g.window_active) {
                    spanBadge.className = 'badge badge-ok';
                    spanBadge.textContent = 'Відкрито';
                } else {
                    spanBadge.className = 'badge badge-warn';
                    spanBadge.textContent = 'Поза вікном';
                }
                tdWindow.appendChild(divWin);
                tdWindow.appendChild(spanBadge);
                tr.appendChild(tdWindow);

                const tdActions = document.createElement('td');
                const btnEnroll = document.createElement('button');
                btnEnroll.className = 'teal';
                btnEnroll.style.padding = '4px 8px';
                btnEnroll.style.fontSize = '12px';
                btnEnroll.style.marginRight = '4px';
                btnEnroll.textContent = 'Записатися';
                btnEnroll.addEventListener('click', () => createWebEnrollment(g.id));
                tdActions.appendChild(btnEnroll);

                const btnClose = document.createElement('button');
                btnClose.className = 'secondary';
                btnClose.style.padding = '4px 8px';
                btnClose.style.fontSize = '12px';
                btnClose.textContent = 'Закрити';
                btnClose.addEventListener('click', () => closeWebGroup(g.id));
                tdActions.appendChild(btnClose);

                tr.appendChild(tdActions);
                tbody.appendChild(tr);
            }
        }
    } catch (err) {
        appendWebOpLog(`Помилка завантаження дисциплін: ${err.message}`);
    }
}

// Головна операція (О1.4, О1.7): POST /v1/enrollments із відображенням 201 Created або Problem Details
async function createWebEnrollment(overrideGroupId = null, overrideStudentId = null) {
    const groupId = overrideGroupId || document.getElementById('webGroupIdSelect').value;
    const headers = getAuthHeaders();
    if (overrideStudentId) {
        headers['X-User-Id'] = overrideStudentId;
    }

    const payload = { group_id: groupId };

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
            lastCreatedHeldEnrollmentId = safeModel.id;
            const btnReg = document.getElementById('btnRegisterWebEnrollmentToRoster');
            if (btnReg) {
                btnReg.style.display = 'inline-block';
                btnReg.textContent = `2. Зареєструвати запис у відомості (POST /registrations)`;
            }
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
    if (activeRegistrations.has(enrollmentId)) {
        appendWebOpLog(
            `Неможливо змінити запис ${enrollmentId}: ` +
            `реєстрація у відомості ще виконується.`
        );
        return;
    }
    try {
        const res = await fetch(`${getApiBase()}/v1/enrollments/${enrollmentId}/cancel`, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify({ reason: 'Скасовано студентом через веб-клієнт' })
        });
        const body = await res.json().catch(() => ({}));
        if (res.ok) {
            appendWebOpLog(`[200 OK] Запис ${enrollmentId} скасовано (status="${body.status}", місце в групі ${body.group_id} повернуто).`);
            if (enrollmentId === lastCreatedHeldEnrollmentId) {
                const btnReg = document.getElementById('btnRegisterWebEnrollmentToRoster');
                if (btnReg) btnReg.style.display = 'none';
                lastCreatedHeldEnrollmentId = null;
            }
        } else {
            appendWebOpLog(`[HTTP ${res.status}] Помилка скасування: ${body.detail || body.title}`);
        }
        await refreshAll();
    } catch (err) {
        appendWebOpLog(`Помилка скасування запису: ${err.message}`);
    }
}

let lastCreatedHeldEnrollmentId = null;
const activeRegistrations = new Set();
const completedTerminalRegistrations = new Map();

function setEnrollmentRowButtonsDisabled(enrollmentId, disabled) {
    const rows = document.querySelectorAll(`tr[data-enrollment-id="${enrollmentId}"]`);
    rows.forEach(tr => {
        tr.querySelectorAll('button').forEach(btn => {
            btn.disabled = disabled;
        });
    });
}

// Наскрізна асинхронна реєстрація Web-запису у відомості деканату через чергу РПО (Web C10: held -> черга РПО -> decanate_roster -> confirmed)
async function registerWebEnrollmentToRoster(enrollmentId = null, triggerBtn = null) {
    const targetId = enrollmentId || lastCreatedHeldEnrollmentId;
    if (!targetId) {
        appendWebOpLog('Помилка: не вказано ідентифікатор Web-запису для реєстрації.');
        return;
    }

    if (activeRegistrations.has(targetId)) {
        appendWebOpLog(`Реєстрація для запису ${targetId} вже виконується. Зачекайте завершення.`);
        return;
    }

    if (completedTerminalRegistrations.has(targetId)) {
        const prev = completedTerminalRegistrations.get(targetId);
        appendWebOpLog(
            `[Увага] Заявка для запису ${targetId} вже завершилася в термінальному стані '${prev.status}' (requestId=${prev.requestId}).\n` +
            `Використовується постійний ключ '${prev.ik}', тому повторне надсилання поверне збережений результат без перезапуску черги.`
        );
        return;
    }

    activeRegistrations.add(targetId);
    setEnrollmentRowButtonsDisabled(targetId, true);

    const btnReg = document.getElementById('btnRegisterWebEnrollmentToRoster');
    const isTargetTopBtn = (targetId === lastCreatedHeldEnrollmentId);

    if (triggerBtn) {
        triggerBtn.disabled = true;
    }
    if (btnReg && isTargetTopBtn) {
        btnReg.disabled = true;
    }

    appendWebOpLog(`[1/3] Реєстрація Web-запису ${targetId} у відомості деканату (POST /v1/enrollments/${targetId}/registrations)...`);
    try {
        const ik = `ik-reg-${targetId}`;
        const headers = getAuthHeaders();
        headers['Idempotency-Key'] = ik;

        const res = await fetch(`${getApiBase()}/v1/enrollments/${targetId}/registrations`, {
            method: 'POST',
            headers
        });

        const rawBody = await res.json().catch(() => ({}));
        if (res.status !== 202) {
            appendWebOpLog(`[HTTP ${res.status}] Помилка взяття в чергу: ${rawBody.detail || rawBody.title || 'Помилка'}`);
            await refreshAll();
            return;
        }

        const requestId = rawBody.id;
        if (!requestId) {
            appendWebOpLog(`[HTTP 202 Accepted] Помилка: відповідь сервера не містить обов'язкового поля 'id' (requestId). Опитування неможливе.`);
            await refreshAll();
            return;
        }

        // Зберігаємо незавершену заявку в localStorage для збереження контексту між перезавантаженнями
        try {
            localStorage.setItem('c10_last_registration', JSON.stringify({
                enrollmentId: targetId,
                requestId,
                status: 'pending',
                timestamp: Date.now()
            }));
        } catch { /* ignore storage errors */ }

        const loc = res.headers.get('Location') || `/v1/enrollment-requests/${requestId}`;
        appendWebOpLog(`[2/3 202 Accepted] Заявку розміщено в черзі РПО: requestId=${requestId}, Location=${loc}.\n  Очікуємо обробку воркером та внесення до decanate_roster...`);

        // Опитування стану через GET /v1/enrollment-requests/{requestId}
        let isDone = false;
        let pollBody = null;
        let transientErrorCount = 0;
        const startTime = Date.now();

        while (!isDone && (Date.now() - startTime < 30000)) {
            await new Promise(r => setTimeout(r, 200));

            let pollRes;
            try {
                pollRes = await fetch(`${getApiBase()}/v1/enrollment-requests/${requestId}`, {
                    headers: getAuthHeaders()
                });
            } catch (netErr) {
                transientErrorCount++;
                if (transientErrorCount > 5) {
                    appendWebOpLog(`Помилка мережі при опитуванні черги: ${netErr.message}`);
                    await refreshAll();
                    return;
                }
                continue;
            }

            if (!pollRes.ok) {
                // Тимчасові помилки (429, 502, 503, 504) дозволяють повторне опитування з урахуванням Retry-After
                if (pollRes.status === 429 || pollRes.status === 502 || pollRes.status === 503 || pollRes.status === 504) {
                    transientErrorCount++;
                    if (transientErrorCount > 5) {
                        appendWebOpLog(`Перевищено ліміт тимчасових помилок шлюзу (HTTP ${pollRes.status}).`);
                        await refreshAll();
                        return;
                    }

                    const retryAfterHeader = pollRes.headers.get('Retry-After');
                    let backoffMs = 200;
                    if (retryAfterHeader) {
                        const parsedSec = parseFloat(retryAfterHeader);
                        if (!isNaN(parsedSec) && parsedSec > 0) {
                            backoffMs = Math.min(parsedSec * 1000, 5000);
                        }
                    }
                    await new Promise(r => setTimeout(r, backoffMs));
                    continue;
                }

                // Постійні помилки (404 Not Found, 400 Bad Request, 401/403, 500 тощо) показуємо користувачеві та перериваємо опрос
                const errBody = await pollRes.json().catch(() => ({}));
                appendWebOpLog(
                    `Помилка опитування черги (HTTP ${pollRes.status}): ${errBody.detail || errBody.title || pollRes.statusText}`
                );
                await refreshAll();
                return;
            }

            transientErrorCount = 0;
            const currentBody = await pollRes.json().catch(() => null);
            if (!currentBody) continue;

            pollBody = currentBody;
            if (pollBody.status === 'done' || pollBody.status === 'rejected' || pollBody.status === 'failed') {
                isDone = true;
                appendWebOpLog(`  Статус заявки в черзі РПО: status="${pollBody.status}", recorded_in_roster=${pollBody.recorded_in_roster}.`);
                break;
            }
        }

        if (!isDone) {
            appendWebOpLog('Реєстрація ще обробляється. Час очікування вичерпано (30 с). Остаточний результат не отримано.');
            await refreshAll();
            return;
        }

        if (pollBody.status !== 'done' ||
            pollBody.recorded_in_roster !== true) {
            completedTerminalRegistrations.set(targetId, {
                status: pollBody.status,
                requestId,
                recorded_in_roster: pollBody.recorded_in_roster,
                ik
            });
            try {
                localStorage.setItem('c10_last_registration', JSON.stringify({
                    enrollmentId: targetId,
                    requestId,
                    status: pollBody.status,
                    recorded_in_roster: pollBody.recorded_in_roster,
                    timestamp: Date.now()
                }));
            } catch { /* ignore */ }

            appendWebOpLog(
                `Помилка реєстрації: status=${pollBody.status}, ` +
                `recorded_in_roster=${pollBody.recorded_in_roster}.\n` +
                `Заявку ${requestId} завершено в термінальному стані (${pollBody.status}). Вона зафіксована з постійним ключем '${ik}', тому повторне надсилання поверне цей самий результат без перезапуску.`
            );
            await refreshAll();
            return;
        }

        completedTerminalRegistrations.set(targetId, {
            status: 'done',
            requestId,
            recorded_in_roster: true,
            ik
        });
        try {
            localStorage.setItem('c10_last_registration', JSON.stringify({
                enrollmentId: targetId,
                requestId,
                status: 'done',
                recorded_in_roster: true,
                timestamp: Date.now()
            }));
        } catch { /* ignore */ }

        // Отримуємо оновлений Web-запис
        const webEnrRes = await fetch(`${getApiBase()}/v1/enrollments/${targetId}`, {
            headers: getAuthHeaders()
        });
        if (!webEnrRes.ok) {
            const errWeb = await webEnrRes.json().catch(() => ({}));
            appendWebOpLog(`Помилка отримання оновленого запису (HTTP ${webEnrRes.status}): ${errWeb.detail || errWeb.title || 'Помилка'}`);
            await refreshAll();
            return;
        }

        const webEnr = await webEnrRes.json();
        if (webEnr.status !== 'confirmed') {
            appendWebOpLog(
                `Помилка: очікувався confirmed, отримано ${webEnr.status}`
            );
            return;
        }

        appendWebOpLog(`[3/3 Успіх] Наскрізний сценарій завершено! Web-запис ${targetId} підтверджено у відомості деканату.\n  Статус="${webEnr.status}", версія=${webEnr.version}.`);

        if (btnReg && isTargetTopBtn) {
            btnReg.style.display = 'none';
        }
        if (isTargetTopBtn) {
            lastCreatedHeldEnrollmentId = null;
        }

        await refreshAll();
    } catch (err) {
        appendWebOpLog(`Помилка реєстрації у відомості: ${err.message}`);
    } finally {
        activeRegistrations.delete(targetId);
        setEnrollmentRowButtonsDisabled(targetId, false);
        if (triggerBtn) {
            triggerBtn.disabled = false;
        }
        if (btnReg && isTargetTopBtn) {
            btnReg.disabled = false;
        }
    }
}

// Підтвердження запису студентом (Web О1.1: held -> confirmed): POST /v1/enrollments/{id}/confirm
async function confirmWebEnrollment(enrollmentId) {
    if (activeRegistrations.has(enrollmentId)) {
        appendWebOpLog(
            `Неможливо змінити запис ${enrollmentId}: ` +
            `реєстрація у відомості ще виконується.`
        );
        return;
    }
    try {
        const res = await fetch(`${getApiBase()}/v1/enrollments/${enrollmentId}/confirm`, {
            method: 'POST',
            headers: getAuthHeaders()
        });
        const body = await res.json().catch(() => ({}));
        if (res.ok) {
            appendWebOpLog(`[200 OK] Запис ${enrollmentId} успішно підтверджено (status="${body.status}").`);
            if (enrollmentId === lastCreatedHeldEnrollmentId) {
                const btnReg = document.getElementById('btnRegisterWebEnrollmentToRoster');
                if (btnReg) btnReg.style.display = 'none';
                lastCreatedHeldEnrollmentId = null;
            }
        } else {
            appendWebOpLog(`[HTTP ${res.status}] Помилка підтвердження: ${body.detail || body.title}`);
        }
        await refreshAll();
    } catch (err) {
        appendWebOpLog(`Помилка підтвердження запису: ${err.message}`);
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
    const seenCursors = new Set();
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

        if (seenCursors.has(body.next_cursor)) {
            appendWebCursorLog(`[Увага] Виявлено повторний курсор (зациклення). Завершення обходу списку.`);
            break;
        }
        seenCursors.add(body.next_cursor);
        cursor = body.next_cursor;
    }

    if (latestMeta) {
        const badge = document.getElementById('webStudentStatusBadge');
        badge.textContent = `Студент: ${latestMeta.student_id} | Активних дисциплін: ${latestMeta.active_count}/${latestMeta.max_disciplines} | Кредитів ЄКТС: ${Number(latestMeta.active_ects).toFixed(1)}/${Number(latestMeta.max_ects).toFixed(1)}`;
    }

    for (const item of allItems) {
        const tr = document.createElement('tr');
        tr.dataset.enrollmentId = item.id;

        const tdId = document.createElement('td');
        tdId.style.fontFamily = 'monospace';
        tdId.style.fontSize = '11px';
        tdId.textContent = item.id;
        tr.appendChild(tdId);

        const tdGroup = document.createElement('td');
        const strongGroup = document.createElement('strong');
        strongGroup.textContent = item.group_id;
        const divTitle = document.createElement('div');
        divTitle.style.fontSize = '11px';
        divTitle.style.color = '#64748b';
        divTitle.textContent = item.discipline_title;
        tdGroup.appendChild(strongGroup);
        tdGroup.appendChild(divTitle);
        tr.appendChild(tdGroup);

        const tdEcts = document.createElement('td');
        tdEcts.textContent = item.ects.toFixed(1);
        tr.appendChild(tdEcts);

        const tdStatus = document.createElement('td');
        const spanStatus = document.createElement('span');
        spanStatus.className = (item.status === 'held' || item.status === 'confirmed') ? 'badge badge-ok' : 'badge badge-warn';
        spanStatus.textContent = `${item.status} (v${item.version})`;
        tdStatus.appendChild(spanStatus);
        tr.appendChild(tdStatus);

        const tdTime = document.createElement('td');
        tdTime.style.fontSize = '11px';
        tdTime.textContent = item.created_at_local;
        tr.appendChild(tdTime);

        const tdInst = document.createElement('td');
        const spanInst = document.createElement('span');
        spanInst.className = 'badge badge-ok';
        spanInst.textContent = item.instance_id;
        tdInst.appendChild(spanInst);
        tr.appendChild(tdInst);

        const isBusy = activeRegistrations.has(item.id);
        const tdActions = document.createElement('td');
        if (item.status === 'held') {
            const btnRoster = document.createElement('button');
            btnRoster.className = 'teal';
            btnRoster.style.padding = '3px 7px';
            btnRoster.style.fontSize = '11px';
            btnRoster.style.marginRight = '4px';
            btnRoster.textContent = 'У відомість';
            btnRoster.title = 'Зареєструвати у відомості деканату через чергу РПО (POST /registrations -> 202 Accepted)';
            btnRoster.disabled = isBusy;
            btnRoster.addEventListener('click', () => registerWebEnrollmentToRoster(item.id, btnRoster));
            tdActions.appendChild(btnRoster);

            const btnConfirm = document.createElement('button');
            btnConfirm.className = 'secondary';
            btnConfirm.style.padding = '3px 7px';
            btnConfirm.style.fontSize = '11px';
            btnConfirm.style.marginRight = '4px';
            btnConfirm.textContent = 'Підтвердити';
            btnConfirm.title = 'Пряме підтвердження студентом без запису до відомості (POST /confirm)';
            btnConfirm.disabled = isBusy;
            btnConfirm.addEventListener('click', () => confirmWebEnrollment(item.id));
            tdActions.appendChild(btnConfirm);
        }
        if (item.status === 'held' || item.status === 'confirmed') {
            const btnCancel = document.createElement('button');
            btnCancel.className = 'danger';
            btnCancel.style.padding = '3px 7px';
            btnCancel.style.fontSize = '11px';
            btnCancel.textContent = 'Скасувати';
            btnCancel.disabled = isBusy;
            btnCancel.addEventListener('click', () => cancelWebEnrollment(item.id));
            tdActions.appendChild(btnCancel);
        } else {
            tdActions.textContent = '—';
        }
        tr.appendChild(tdActions);

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
// 1. Часове вікно [opens_at, closes_at): запит поза вікном -> 409 Conflict
// 2. Ліміт ЄКТС: сума 30.0 -> 201 Created, сума 30.5 -> 409 Conflict
async function runWebBoundaryTests() {
    appendWebOpLog(`=== Старт автотесту граничних умов Д1.1 та Д1.5 ===`);
    appendWebOpLog(`1. Перевірка вікна запису: спроба запису в DISC-WINDOW-TEST поза вікном -> 409 Conflict...`);
    await createWebEnrollment('DISC-WINDOW-TEST', 'student-2');
    appendWebOpLog(`2. Перевірка ліміту ЄКТС: студент із 20.0 ЄКТС пробує 10.5 ЄКТС (20.0 + 10.5 = 30.5 > 30.0 -> 409 Conflict)...`);
    await createWebEnrollment('DISC-ECTS-10-5-G1', 'student-ects-20');
    appendWebOpLog(`3. Перевірка ліміту ЄКТС: студент із 20.0 ЄКТС записується на 10.0 ЄКТС (20.0 + 10.0 = 30.0 <= 30.0 -> 201 Created)...`);
    await createWebEnrollment('DISC-ECTS-10-G1', 'student-ects-20');
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
    await createWebEnrollment('DISC-DB-01', 'student-limit-max');
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
            const invHolds = Boolean(g.invariant_holds ?? g.invariantHolds);
            const badgeClass = invHolds ? 'badge-ok' : 'badge-err';
            const taken = g.taken;
            const seats = g.seats;
            const badgeText = invHolds
                ? `Дотримано (${taken} <= ${seats})`
                : `ПОРУШЕНО (${taken} > ${seats})`;

            const tdId = document.createElement('td');
            const strongId = document.createElement('strong');
            strongId.textContent = g.group_id || g.groupId || '';
            tdId.appendChild(strongId);
            tr.appendChild(tdId);

            const tdTitle = document.createElement('td');
            tdTitle.textContent = g.discipline_title || g.disciplineTitle || '';
            tr.appendChild(tdTitle);

            const tdSeats = document.createElement('td');
            tdSeats.textContent = String(seats);
            tr.appendChild(tdSeats);

            const tdTaken = document.createElement('td');
            const strongTaken = document.createElement('strong');
            strongTaken.textContent = String(taken);
            tdTaken.appendChild(strongTaken);
            tr.appendChild(tdTaken);

            const tdStatus = document.createElement('td');
            const spanStatus = document.createElement('span');
            spanStatus.className = `badge ${badgeClass}`;
            spanStatus.textContent = badgeText;
            tdStatus.appendChild(spanStatus);
            tr.appendChild(tdStatus);

            tbody.appendChild(tr);
        }
    } catch (err) {
        appendLog(`Помилка завантаження груп: ${err.message}`);
    }
}

async function loadMetrics() {
    try {
        const [res, healthRes] = await Promise.all([
            fetch(`${getApiBase()}/metrics?format=json`, { headers: getAuthHeaders() }),
            fetch(`${getApiBase()}/health`)
        ]);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        const healthData = healthRes.ok ? await healthRes.json().catch(() => ({})) : {};

        const overridesBadge = document.getElementById('testOverridesBadge');
        if (overridesBadge) {
            const enabled = Boolean(healthData.allow_test_overrides ?? data.configuration?.allow_test_overrides);
            overridesBadge.className = enabled ? 'badge badge-warn' : 'badge badge-ok';
            overridesBadge.textContent = enabled
                ? 'ALLOW_TEST_OVERRIDES=true (Тестовий стенд)'
                : 'ALLOW_TEST_OVERRIDES=false (Робочий захищений режим)';
        }

        const summary = document.getElementById('metricsSummary');
        const modeBadge = (data.configuration?.unsafe_mode ?? false)
            ? '<span class="badge badge-err">UNSAFE (Без блокувань)</span>'
            : '<span class="badge badge-ok">SAFE (Транзакційний)</span>';

        summary.innerHTML = `
            <div class="stat-box">
                <div class="stat-label">У черзі / В обробці</div>
                <div class="stat-val">${escapeHtml(data.queue_length)} / ${escapeHtml(data.in_processing)}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Успішно (done) / Відмов (rejected)</div>
                <div class="stat-val">${escapeHtml(data.counters?.total_done)} / ${escapeHtml(data.counters?.total_rejected)}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Відмов черги (HTTP 429)</div>
                <div class="stat-val">${escapeHtml(data.counters?.total_429_too_many_requests)}</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Очікування в черзі p95</div>
                <div class="stat-val">${escapeHtml(data.intervals_ms?.queue_wait_ms?.p95_ms ?? '—')} мс</div>
            </div>
            <div class="stat-box">
                <div class="stat-label">Літтл: L_теор / L_інт / L_100мс</div>
                <div class="stat-val" style="font-size: 14px;">${escapeHtml(data.littles_law?.l_theoretical_avg_queue_length)} / ${escapeHtml(data.littles_law?.l_observed_avg_queue_length)} / ${escapeHtml(data.littles_law?.l_observed_independent_sampled ?? '—')}</div>
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
        resultBox.textContent = 'Надсилання запиту...';
        const res = await fetch(`${getApiBase()}/v1/enrollment-requests`, {
            method: 'POST',
            headers: getAuthHeaders(),
            body: JSON.stringify({ student_id: studentId, group_id: groupId })
        });

        const body = await res.json().catch(() => ({}));
        if (res.status !== 202) {
            resultBox.innerHTML = `<span class="badge badge-err">Помилка HTTP ${escapeHtml(res.status)}</span> ${escapeHtml(body.detail || body.title || '')}`;
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

            const reason = statusData.rejection_reason || statusData.rejectionReason;
            const qWait = statusData.queue_wait_ms ?? statusData.queueWaitMs ?? '—';
            const proc = statusData.processing_time_ms ?? statusData.processingTimeMs ?? '—';
            const deliv = statusData.delivery_wait_ms ?? statusData.deliveryWaitMs ?? '—';

            resultBox.innerHTML = `
                <div><strong>ID заявки:</strong> ${escapeHtml(statusData.id)}</div>
                <div><strong>Статус:</strong> <span class="badge badge-warn">${escapeHtml(statusData.status)}</span></div>
                ${reason ? `<div><strong>Причина:</strong> ${escapeHtml(reason)}</div>` : ''}
                <div><strong>Очікування в черзі:</strong> ${escapeHtml(qWait)} мс |
                     <strong>Обробка:</strong> ${escapeHtml(proc)} мс |
                     <strong>Очікування видачі (GET):</strong> ${escapeHtml(deliv)} мс</div>
            `;

            if (['done', 'rejected', 'failed'].includes(statusData.status)) {
                clearInterval(pollTimer);
                appendLog(`Заявка ${reqId} завершилась у стані '${statusData.status}' (first_read_at зафіксовано).`);
                await refreshAll();
            }
        }, 250);
    } catch (err) {
        resultBox.innerHTML = `<span class="badge badge-err">Помилка мережі: ${escapeHtml(err.message)}</span>`;
    }
}

async function runStressBatch() {
    const groupId = document.getElementById('stressGroup').value;
    const count = parseInt(document.getElementById('stressCount').value, 10) || 100;

    appendLog(`Старт пакетного навантаження: ${count} одночасних запитів у групу ${groupId}...`);

    const requests = [];
    for (let i = 1; i <= count; i++) {
        const studentId = 'student-safe-' + String(((i - 1) % 20) + 1).padStart(2, '0');
        requests.push(
            fetch(`${getApiBase()}/v1/enrollment-requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify({ student_id: studentId, group_id: groupId })
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
            const studentId = `student-safe-${String(((reqCounter - 1) % 20) + 1).padStart(2, '0')}`;
            fetch(`${getApiBase()}/v1/enrollment-requests`, {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify({ student_id: studentId, group_id: groupId })
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

    const metricsRes = await fetch(`${getApiBase()}/metrics?format=json`, { headers: getAuthHeaders() });
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
    // Прив'язка обробників подій до кнопок та селектів без inline обробників (О1.7, Д1.2)
    document.getElementById('btnRefreshAll')?.addEventListener('click', () => refreshAll());
    document.getElementById('btnCreateWebEnrollment')?.addEventListener('click', () => createWebEnrollment());
    document.getElementById('btnRegisterWebEnrollmentToRoster')?.addEventListener('click', (e) => registerWebEnrollmentToRoster(null, e.currentTarget));
    document.getElementById('btnRunWebBoundaryTests')?.addEventListener('click', () => runWebBoundaryTests());
    document.getElementById('btnRunWebProblemDetailsTests')?.addEventListener('click', () => runWebProblemDetailsTests());
    document.getElementById('btnReadAllWebEnrollmentPages')?.addEventListener('click', () => readAllWebEnrollmentPages());
    document.getElementById('btnTestCorruptedCursor')?.addEventListener('click', () => testCorruptedCursor());
    document.getElementById('btnResetSafe')?.addEventListener('click', () => resetEnvironment(false));
    document.getElementById('btnResetUnsafe')?.addEventListener('click', () => resetEnvironment(true));
    document.getElementById('btnSubmitSingleEnrollment')?.addEventListener('click', () => submitSingleEnrollment());
    document.getElementById('btnRunStressBatch')?.addEventListener('click', () => runStressBatch());
    document.getElementById('btnRunLittlesLaw')?.addEventListener('click', () => runLittlesLawExperiment());
    document.getElementById('webUserSelect')?.addEventListener('change', () => refreshAll());

    refreshAll();
    try {
        const savedReg = JSON.parse(localStorage.getItem('c10_last_registration') || 'null');
        if (savedReg && savedReg.requestId) {
            appendWebOpLog(`[Сесія] Збережено останню реєстрацію: enrollmentId=${savedReg.enrollmentId}, requestId=${savedReg.requestId}, статус="${savedReg.status}".`);
        }
    } catch { /* ignore */ }
    setInterval(() => {
        loadGroups();
        loadMetrics();
    }, 3000);
});
