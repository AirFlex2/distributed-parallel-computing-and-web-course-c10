-- Міграція 005: Композитні зовнішні ключі для усвідомленої денормалізації (3NF -> BCNF з гарантією цілісності)
-- Поле discipline_id у таблицях enrollment та decanate_roster денормалізовано задля підтримки
-- часткового унікального індексу (student_id, discipline_id) WHERE status IN ('held', 'confirmed').
-- Композитні зовнішні ключі (group_id, discipline_id) -> discipline_group(id, discipline_id)
-- математично усувають будь-яку аномалію узгодженості між групою та дисципліною.

DO $$
BEGIN
    DELETE FROM decanate_roster WHERE group_id = 'DISC-TEST-UNSAFE';
    DELETE FROM enrollment WHERE group_id = 'DISC-TEST-UNSAFE';
    DELETE FROM enrollment_requests WHERE group_id = 'DISC-TEST-UNSAFE';
    DELETE FROM discipline_group WHERE id = 'DISC-TEST-UNSAFE';

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'uq_discipline_group_id_discipline'
    ) THEN
        ALTER TABLE discipline_group
            ADD CONSTRAINT uq_discipline_group_id_discipline UNIQUE (id, discipline_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_enrollment_group_discipline'
    ) THEN
        ALTER TABLE enrollment
            ADD CONSTRAINT fk_enrollment_group_discipline
            FOREIGN KEY (group_id, discipline_id)
            REFERENCES discipline_group(id, discipline_id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_decanate_roster_group_discipline'
    ) THEN
        ALTER TABLE decanate_roster
            ADD CONSTRAINT fk_decanate_roster_group_discipline
            FOREIGN KEY (group_id, discipline_id)
            REFERENCES discipline_group(id, discipline_id)
            ON DELETE CASCADE;
    END IF;
END $$;
