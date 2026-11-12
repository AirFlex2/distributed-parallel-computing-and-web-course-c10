-- Міграція 007: Додавання прапорця dpc_sandbox_enabled для суворого ізолювання автономного DPC-стенду (РПО)
-- Автономний контур РПО (POST /v1/enrollment-requests) дозволений виключно для виділених груп стенду (DISC-DPC-01, DISC-TEST-SAFE).
-- Інтерактивні групи Web-контуру (наприклад, DISC-WEB-01, DISC-AI-02) захищені від випадкового списання через чергу РПО.

ALTER TABLE discipline_group
    ADD COLUMN IF NOT EXISTS dpc_sandbox_enabled BOOLEAN NOT NULL DEFAULT FALSE;

-- Увімкнення автономного DPC-стенду для виділених груп ЛР1:
UPDATE discipline_group
SET dpc_sandbox_enabled = TRUE
WHERE id IN ('DISC-DPC-01', 'DISC-TEST-SAFE');
