-- Міграція 001: Чиста узгоджена схема бази даних PostgreSQL для теми С10 «Запис на вибіркові дисципліни»
-- Об'єднує за розділом 1.10 (РПО) / 1.9 (Web) спільний ресурс місць у групах:
--   1. Курс «Обмін даними у Web-застосунках» (ЛР1, О1.1–О1.2: discipline, discipline_group, student, enrollment)
--   2. Курс «Розподілені та паралельні обчислення» (ЛР1, О1.1, О1.4: enrollment_requests, decanate_roster, VIEW group_seats)

CREATE TABLE IF NOT EXISTS discipline (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    ects NUMERIC(3,1) NOT NULL CHECK (ects > 0.0 AND ects <= 30.0),
    is_test_only BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS discipline_group (
    id TEXT PRIMARY KEY,
    discipline_id TEXT NOT NULL REFERENCES discipline(id) ON DELETE CASCADE,
    group_code TEXT NOT NULL UNIQUE,
    seats INT NOT NULL CHECK (seats > 0),
    available INT NOT NULL,
    opens_at TIMESTAMPTZ NOT NULL,
    closes_at TIMESTAMPTZ NOT NULL,
    university_tz TEXT NOT NULL DEFAULT 'Europe/Kyiv',
    closed BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT chk_discipline_group_window CHECK (closes_at > opens_at),
    CONSTRAINT chk_discipline_group_available CHECK (available >= 0 AND available <= seats)
);

CREATE TABLE IF NOT EXISTS student (
    id TEXT PRIMARY KEY,
    email TEXT NOT NULL UNIQUE,
    full_name TEXT NOT NULL,
    max_disciplines INT NOT NULL DEFAULT 5 CHECK (max_disciplines > 0 AND max_disciplines <= 10),
    active_count INT NOT NULL DEFAULT 0 CHECK (active_count >= 0 AND active_count <= max_disciplines),
    active_ects NUMERIC(3,1) NOT NULL DEFAULT 0.0 CHECK (active_ects >= 0.0 AND active_ects <= 30.0)
);

CREATE TABLE IF NOT EXISTS enrollment (
    id UUID PRIMARY KEY,
    student_id TEXT NOT NULL REFERENCES student(id) ON DELETE CASCADE,
    group_id TEXT NOT NULL REFERENCES discipline_group(id) ON DELETE CASCADE,
    discipline_id TEXT NOT NULL REFERENCES discipline(id) ON DELETE CASCADE,
    status TEXT NOT NULL CHECK (status IN ('held', 'confirmed', 'expired', 'cancelled')),
    version INT NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    cancelled_at TIMESTAMPTZ NULL,
    CONSTRAINT chk_enrollment_expiry CHECK (expires_at > created_at)
);

-- Унікальний частковий індекс: студент не може двічі мати активний запис (held або confirmed) на ту саму дисципліну
CREATE UNIQUE INDEX IF NOT EXISTS idx_enrollment_active_student_discipline
    ON enrollment(student_id, discipline_id)
    WHERE status IN ('held', 'confirmed');

-- Індекс для курсорного пагінування списку записів студента за парою (created_at DESC, id DESC) (О1.5 Web)
CREATE INDEX IF NOT EXISTS idx_enrollment_cursor
    ON enrollment(student_id, created_at DESC, id DESC);

-- Таблиця асинхронних заявок черги для курсу «Розподілені та паралельні обчислення» (О1.1 РПО)
-- Зовнішні ключі гарантують цілісність даних: заявки створюються лише для дійсних студентів і груп (п. 1)
CREATE TABLE IF NOT EXISTS enrollment_requests (
    id UUID PRIMARY KEY,
    student_id TEXT NOT NULL REFERENCES student(id) ON DELETE CASCADE,
    group_id TEXT NOT NULL REFERENCES discipline_group(id) ON DELETE CASCADE,
    assigned_group_id TEXT NULL REFERENCES discipline_group(id) ON DELETE SET NULL,
    status TEXT NOT NULL CHECK (status IN ('queued', 'processing', 'done', 'rejected', 'failed')),
    rejection_reason TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ NULL,
    finished_at TIMESTAMPTZ NULL,
    first_read_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_enrollment_requests_group_id ON enrollment_requests(group_id);
CREATE INDEX IF NOT EXISTS idx_enrollment_requests_status ON enrollment_requests(status);

-- Відомість деканату (decanate_roster) — журнал предметного ефекту РПО за ключем request_id
CREATE TABLE IF NOT EXISTS decanate_roster (
    request_id UUID PRIMARY KEY REFERENCES enrollment_requests(id) ON DELETE CASCADE,
    student_id TEXT NOT NULL REFERENCES student(id) ON DELETE CASCADE,
    group_id TEXT NOT NULL REFERENCES discipline_group(id) ON DELETE CASCADE,
    discipline_id TEXT NOT NULL REFERENCES discipline(id) ON DELETE CASCADE,
    ects NUMERIC(3,1) NOT NULL CHECK (ects > 0.0 AND ects <= 30.0),
    enrollment_id UUID NULL REFERENCES enrollment(id) ON DELETE SET NULL,
    assignment_type TEXT NOT NULL DEFAULT 'primary_roster',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_decanate_roster_group_id ON decanate_roster(group_id);
CREATE INDEX IF NOT EXISTS idx_decanate_roster_student_id ON decanate_roster(student_id);

-- Єдине прозоре джерело істини для місць у групі (§1.10 РПО / §1.9 Web):
-- taken = seats - available як єдина правда стану місць, roster_count — зафіксовані в журналі деканату
DROP VIEW IF EXISTS group_seats;

CREATE VIEW group_seats AS
SELECT
    g.id AS group_id,
    d.title AS discipline_title,
    g.seats,
    (g.seats - g.available) AS taken,
    g.available AS free_seats,
    (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id) AS roster_count,
    g.closed,
    g.opens_at,
    g.closes_at,
    g.university_tz
FROM discipline_group g
JOIN discipline d ON d.id = g.discipline_id;
