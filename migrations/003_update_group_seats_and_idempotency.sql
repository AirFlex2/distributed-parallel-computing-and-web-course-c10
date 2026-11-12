-- Міграція 003: Оновлення представлення group_seats (GREATEST для taken) та підтримка строкового Idempotency-Key

DROP VIEW IF EXISTS group_seats;

CREATE VIEW group_seats AS
SELECT
    g.id AS group_id,
    d.title AS discipline_title,
    g.seats,
    GREATEST(g.seats - g.available, (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id)) AS taken,
    g.available AS free_seats,
    (SELECT COUNT(*)::INT FROM decanate_roster dr WHERE dr.group_id = g.id) AS roster_count,
    g.closed,
    g.opens_at,
    g.closes_at,
    g.university_tz
FROM discipline_group g
JOIN discipline d ON d.id = g.discipline_id;

ALTER TABLE enrollment_requests
    ADD COLUMN IF NOT EXISTS idempotency_key TEXT NULL,
    ADD COLUMN IF NOT EXISTS request_payload_hash TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_enrollment_requests_idempotency
    ON enrollment_requests(student_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;
