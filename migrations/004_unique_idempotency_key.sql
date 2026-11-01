-- Міграція 004: Глобальний унікальний індекс для Idempotency-Key

CREATE UNIQUE INDEX IF NOT EXISTS idx_enrollment_requests_idempotency
    ON enrollment_requests(idempotency_key)
    WHERE idempotency_key IS NOT NULL;
