-- Міграція 001: Єдина узгоджена схема бази даних PostgreSQL для теми С10 «Запис на вибіркові дисципліни»
-- Об'єднує за розділом 1.10 (РПО) / 1.9 (Web) спільний ресурс місць у групах без дублювання:
--   1. Курс «Обмін даними у Web-застосунках» (ЛР1, О1.1–О1.2: discipline, discipline_group, student, enrollment)
--   2. Курс «Розподілені та паралельні обчислення» (ЛР1, О1.1, О1.4: enrollment_requests та представлення VIEW group_seats)

CREATE TABLE IF NOT EXISTS discipline (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    ects NUMERIC(4,1) NOT NULL CHECK (ects > 0.0 AND ects <= 30.0),
    is_test_only BOOLEAN NOT NULL DEFAULT FALSE
);

ALTER TABLE discipline ADD COLUMN IF NOT EXISTS is_test_only BOOLEAN NOT NULL DEFAULT FALSE;

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
    allow_overcommit_unsafe BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT chk_discipline_group_window CHECK (closes_at > opens_at),
    CONSTRAINT chk_discipline_group_available CHECK (available <= seats AND (available >= 0 OR allow_overcommit_unsafe = TRUE))
);

ALTER TABLE discipline_group ADD COLUMN IF NOT EXISTS allow_overcommit_unsafe BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE discipline_group DROP CONSTRAINT IF EXISTS discipline_group_available_check;
ALTER TABLE discipline_group DROP CONSTRAINT IF EXISTS chk_discipline_group_available;
ALTER TABLE discipline_group ADD CONSTRAINT chk_discipline_group_available
    CHECK (available <= seats AND (available >= 0 OR allow_overcommit_unsafe = TRUE));

CREATE TABLE IF NOT EXISTS student (
    id TEXT PRIMARY KEY,
    email TEXT NOT NULL UNIQUE,
    full_name TEXT NOT NULL,
    max_disciplines INT NOT NULL DEFAULT 5 CHECK (max_disciplines > 0 AND max_disciplines <= 10),
    active_count INT NOT NULL DEFAULT 0 CHECK (active_count >= 0 AND active_count <= max_disciplines),
    active_ects NUMERIC(4,1) NOT NULL DEFAULT 0.0 CHECK (active_ects >= 0.0 AND active_ects <= 30.0)
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
    CONSTRAINT chk_enrollment_expiry CHECK (expires_at > created_at),
    CONSTRAINT uq_enrollment_student_group_created UNIQUE (student_id, group_id, created_at)
);

-- Унікальний частковий індекс: студент не може двічі мати активний запис (held або confirmed) на ту саму дисципліну
CREATE UNIQUE INDEX IF NOT EXISTS idx_enrollment_active_student_discipline
    ON enrollment(student_id, discipline_id)
    WHERE status IN ('held', 'confirmed');

-- Індекс для курсорного пагінування списку записів студента за парою (created_at DESC, id DESC) (О1.5 Web)
CREATE INDEX IF NOT EXISTS idx_enrollment_cursor
    ON enrollment(student_id, created_at DESC, id DESC);

-- Таблиця асинхронних заявок черги для курсу «Розподілені та паралельні обчислення» (О1.1 РПО)
-- Відсутність зовнішнього ключа на group_id дозволяє приймати заявки з неіснуючою групою в чергу (202)
-- і переводити їх воркером у регламентний стан 'failed'.
CREATE TABLE IF NOT EXISTS enrollment_requests (
    id UUID PRIMARY KEY,
    student_id TEXT NOT NULL,
    group_id TEXT NOT NULL,
    second_choice_group_id TEXT NULL,
    assigned_group_id TEXT NULL,
    status TEXT NOT NULL CHECK (status IN ('queued', 'processing', 'done', 'rejected', 'failed')),
    rejection_reason TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ NULL,
    finished_at TIMESTAMPTZ NULL,
    first_read_at TIMESTAMPTZ NULL
);

ALTER TABLE enrollment_requests DROP CONSTRAINT IF EXISTS enrollment_requests_group_id_fkey;
ALTER TABLE enrollment_requests ADD COLUMN IF NOT EXISTS second_choice_group_id TEXT NULL;
ALTER TABLE enrollment_requests ADD COLUMN IF NOT EXISTS assigned_group_id TEXT NULL;

CREATE INDEX IF NOT EXISTS idx_enrollment_requests_group_id ON enrollment_requests(group_id);
CREATE INDEX IF NOT EXISTS idx_enrollment_requests_status ON enrollment_requests(status);
CREATE INDEX IF NOT EXISTS idx_enrollment_requests_second_choice ON enrollment_requests(group_id, second_choice_group_id);

-- Відомість деканату (decanate_roster) — предметний ефект обробника РПО за ключем request_id (С10 §3.12 п. 2 та п. 6 РПО):
-- Первинний запис у групу в момент відкриття приймає виключно Web і зменшує discipline_group.available;
-- воркер РПО не резервує місця повторно, а фіксує результат обробки заявки у відомості деканату за request_id,
-- а після закриття запису розподільник РПО призначає студентів у групи другого вибору (second_choice_group_id).
CREATE TABLE IF NOT EXISTS decanate_roster (
    request_id UUID PRIMARY KEY REFERENCES enrollment_requests(id) ON DELETE CASCADE,
    student_id TEXT NOT NULL,
    group_id TEXT NOT NULL REFERENCES discipline_group(id) ON DELETE CASCADE,
    discipline_id TEXT NOT NULL REFERENCES discipline(id) ON DELETE CASCADE,
    ects NUMERIC(4,1) NOT NULL CHECK (ects > 0.0 AND ects <= 30.0),
    enrollment_id UUID NULL REFERENCES enrollment(id) ON DELETE SET NULL,
    assignment_type TEXT NOT NULL DEFAULT 'primary_roster' CHECK (assignment_type IN ('primary_roster', 'second_choice')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_decanate_roster_group_id ON decanate_roster(group_id);
CREATE INDEX IF NOT EXISTS idx_decanate_roster_student_id ON decanate_roster(student_id);

-- Єдине джерело істини для місць у групі (§1.10 РПО / §1.9 Web):
-- замість дублюючої таблиці group_seats використовується SQL-представлення (VIEW) поверх discipline_group та decanate_roster.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'group_seats') THEN
        DROP TABLE group_seats CASCADE;
    END IF;
END $$;

DROP VIEW IF EXISTS group_seats;

CREATE VIEW group_seats AS
SELECT
    g.id AS group_id,
    d.title AS discipline_title,
    g.seats,
    GREATEST(
        g.seats - g.available,
        (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id)
    ) AS taken,
    GREATEST(
        0,
        g.seats - GREATEST(
            g.seats - g.available,
            (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id)
        )
    ) AS free_seats,
    (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id) AS roster_count,
    g.closed,
    g.opens_at,
    g.closes_at,
    g.university_tz,
    g.allow_overcommit_unsafe
FROM discipline_group g
JOIN discipline d ON d.id = g.discipline_id;
