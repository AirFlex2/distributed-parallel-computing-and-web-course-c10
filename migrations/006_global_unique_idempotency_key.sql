-- Міграція 006: Глобальний унікальний індекс для Idempotency-Key

DROP INDEX IF EXISTS idx_enrollment_requests_idempotency;

CREATE UNIQUE INDEX idx_enrollment_requests_idempotency_global
    ON enrollment_requests(idempotency_key)
    WHERE idempotency_key IS NOT NULL;
