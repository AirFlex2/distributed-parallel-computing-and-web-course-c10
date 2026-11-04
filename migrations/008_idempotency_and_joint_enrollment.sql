-- Міграція 008: Ідемпотентність для Web-записів (enrollment) та підтримка enrollment_id у заявках РПО
-- 1. Додавання полів idempotency_key та request_payload_hash до таблиці enrollment для підтримки Idempotency-Key
--    з поверненням збереженого запису (201 Created) або 409 Conflict при зміні тіла (О2.2 / О1.4 Web).
ALTER TABLE enrollment
    ADD COLUMN IF NOT EXISTS idempotency_key TEXT NULL,
    ADD COLUMN IF NOT EXISTS request_payload_hash TEXT NULL;

-- 2. Глобальний унікальний індекс для швидкого пошуку та запобігання дублікатам за Idempotency-Key:
CREATE UNIQUE INDEX IF NOT EXISTS idx_enrollment_idempotency_global
    ON enrollment(idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- 3. Додавання зовнішнього ключа enrollment_id до таблиці enrollment_requests для підтримки
--    наскрізного міжкурсового сценарію Web + DPC (тема C10):
ALTER TABLE enrollment_requests
    ADD COLUMN IF NOT EXISTS enrollment_id UUID NULL REFERENCES enrollment(id) ON DELETE SET NULL;
