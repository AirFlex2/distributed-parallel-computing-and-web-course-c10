-- =============================================================================
-- Міграція 012: Таблиці автентифікації Web ЛР3 та журналу атомарних ефектів Ray РПО ЛР3
-- Web О3.1, О3.2 | РПО О3.1, О3.3, О3.4
-- =============================================================================

CREATE TABLE IF NOT EXISTS web_users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    email TEXT NOT NULL,
    full_name TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('user', 'staff')),
    password_hash TEXT NOT NULL,
    password_salt TEXT NOT NULL,
    student_id TEXT NULL REFERENCES student(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Автоматична синхронізація студентів у таблицю облікових записів web_users
CREATE OR REPLACE FUNCTION fn_sync_student_to_web_users()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO web_users (id, username, email, full_name, role, password_hash, password_salt, student_id, created_at)
    VALUES (
        NEW.id,
        NEW.id,
        NEW.email,
        NEW.full_name,
        'user',
        '33tUlifjf1G54gWVByGgrPvspq7ZTDxo1thM3Y91Nhg=',
        'AQIDBAUGBwgJCgsMDQ4PEA==',
        NEW.id,
        NOW()
    )
    ON CONFLICT (id) DO UPDATE SET
        email = EXCLUDED.email,
        full_name = EXCLUDED.full_name;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sync_student_to_web_users ON student;
CREATE TRIGGER trg_sync_student_to_web_users
AFTER INSERT OR UPDATE ON student
FOR EACH ROW
EXECUTE FUNCTION fn_sync_student_to_web_users();

-- Заповнення облікових записів для наявних студентів
INSERT INTO web_users (id, username, email, full_name, role, password_hash, password_salt, student_id, created_at)
SELECT
    s.id,
    s.id,
    s.email,
    s.full_name,
    'user',
    '33tUlifjf1G54gWVByGgrPvspq7ZTDxo1thM3Y91Nhg=',
    'AQIDBAUGBwgJCgsMDQ4PEA==',
    s.id,
    NOW()
FROM student s
ON CONFLICT (id) DO NOTHING;

-- Демонстраційні облікові записи персоналу деканату (role = 'staff')
INSERT INTO web_users (id, username, email, full_name, role, password_hash, password_salt, student_id, created_at)
VALUES
    (
        'staff-1',
        'staff-1',
        'staff-1@inf.edu.ua',
        'Працівник деканату (staff-1)',
        'staff',
        'Mnv8tm9ZjHhXl2+1zbND7R7QbaBv2MvK/MuL0X73d3k=',
        'FRYXGBkaGxwdHh8gISIjJA==',
        NULL,
        NOW()
    ),
    (
        'deanery',
        'deanery',
        'deanery@inf.edu.ua',
        'Деканат ФІТ (deanery)',
        'staff',
        'Mnv8tm9ZjHhXl2+1zbND7R7QbaBv2MvK/MuL0X73d3k=',
        'FRYXGBkaGxwdHh8gISIjJA==',
        NULL,
        NOW()
    )
ON CONFLICT (id) DO NOTHING;

-- Серверне сховище хешів непрозорих Bearer-токенів Web-сесій (О3.1)
CREATE TABLE IF NOT EXISTS web_auth_tokens (
    token_hash TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES web_users(id) ON DELETE CASCADE,
    username TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('user', 'staff')),
    student_id TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_web_auth_tokens_user_id ON web_auth_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_web_auth_tokens_expires_at ON web_auth_tokens(expires_at);

-- Журнал застосованих предметних ефектів за ключем request_id (РПО О3.3, О3.4)
CREATE TABLE IF NOT EXISTS dpc_applied_effects (
    request_id UUID PRIMARY KEY REFERENCES enrollment_requests(id) ON DELETE CASCADE,
    student_id TEXT NOT NULL,
    group_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('done', 'rejected')),
    rejection_reason TEXT NULL,
    assigned_group_id TEXT NULL,
    roster_recorded BOOLEAN NOT NULL DEFAULT FALSE,
    delivery_count INT NOT NULL DEFAULT 1 CHECK (delivery_count >= 1),
    ray_attempt INT NOT NULL DEFAULT 1,
    worker_id TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
