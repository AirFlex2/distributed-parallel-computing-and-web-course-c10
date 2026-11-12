-- 011_web_idempotency_lr2.sql
-- Розширення таблиці enrollment для повного механізму ідемпотентності Web О2.2
-- (збереження операції, стану обробки processing/completed, початкової HTTP-відповіді та TTL/expires_at у тій самій транзакції)
-- та ізольовані тестові дані для багатопримірникового експерименту конкурентності Web О2.1 (група на 25 місць + 60 студентів).

ALTER TABLE enrollment
    ADD COLUMN IF NOT EXISTS idempotency_operation TEXT NOT NULL DEFAULT 'POST /v1/enrollments',
    ADD COLUMN IF NOT EXISTS idempotency_state TEXT NOT NULL DEFAULT 'completed',
    ADD COLUMN IF NOT EXISTS idempotency_response_status INT NOT NULL DEFAULT 201,
    ADD COLUMN IF NOT EXISTS idempotency_response_body TEXT NULL,
    ADD COLUMN IF NOT EXISTS idempotency_response_location TEXT NULL,
    ADD COLUMN IF NOT EXISTS idempotency_expires_at TIMESTAMPTZ NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'chk_enrollment_idempotency_state'
          AND conrelid = 'enrollment'::regclass
    ) THEN
        ALTER TABLE enrollment
            ADD CONSTRAINT chk_enrollment_idempotency_state
            CHECK (idempotency_state IN ('processing', 'completed'));
    END IF;
END $$;

UPDATE enrollment
SET idempotency_response_location = COALESCE(idempotency_response_location, '/v1/enrollments/' || id::text),
    idempotency_expires_at = COALESCE(idempotency_expires_at, created_at + INTERVAL '24 hours')
WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_enrollment_idempotency_expires_at
    ON enrollment(idempotency_expires_at)
    WHERE idempotency_key IS NOT NULL;

-- Тестова дисципліна та група на 25 місць для експерименту конкурентності Web О2.1 (ізольована від публічного каталогу через is_test_only = TRUE)
INSERT INTO discipline (id, code, title, ects, is_test_only)
VALUES (
    '11111111-1111-1111-1111-111111111109',
    'DISC-WEB-CONC-25',
    'Тестовий полігон конкурентності Web О2.1 (25 місць)',
    3.0,
    TRUE
)
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    title = EXCLUDED.title,
    ects = EXCLUDED.ects,
    is_test_only = EXCLUDED.is_test_only;

INSERT INTO discipline_group (
    id,
    discipline_id,
    group_code,
    seats,
    available,
    opens_at,
    closes_at,
    university_tz,
    closed,
    dpc_sandbox_enabled
)
VALUES (
    '22222222-2222-2222-2222-222222222209',
    '11111111-1111-1111-1111-111111111109',
    'WEB-CONC-25',
    25,
    25,
    TIMESTAMPTZ '2025-01-01 00:00:00+02',
    TIMESTAMPTZ '2027-12-31 23:59:59+02',
    'Europe/Kyiv',
    FALSE,
    FALSE
)
ON CONFLICT (id) DO UPDATE
SET discipline_id = EXCLUDED.discipline_id,
    group_code = EXCLUDED.group_code,
    seats = EXCLUDED.seats,
    available = EXCLUDED.available,
    opens_at = EXCLUDED.opens_at,
    closes_at = EXCLUDED.closes_at,
    university_tz = EXCLUDED.university_tz,
    closed = EXCLUDED.closed,
    dpc_sandbox_enabled = EXCLUDED.dpc_sandbox_enabled;

-- 60 виділених студентів для конкурентного навантаження Web О2.1 (student-web-conc-01 .. student-web-conc-60)
INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
SELECT
    'student-web-conc-' || LPAD(gs::text, 2, '0'),
    'student.web.conc.' || LPAD(gs::text, 2, '0') || '@univ.edu.ua',
    'Студент Конкурентного Тесту Web ' || LPAD(gs::text, 2, '0'),
    5,
    0,
    0.0
FROM generate_series(1, 60) AS gs
ON CONFLICT (id) DO NOTHING;
