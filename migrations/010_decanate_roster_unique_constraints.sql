-- Міграція 010: Додаткові обмеження цілісності та унікальності для відомості деканату (decanate_roster)
-- 1. Унікальний індекс за парою (student_id, discipline_id), що гарантує на рівні БД,
--    що студент не може бути двічі внесений до відомості з однієї дисципліни (§1.10 РПО / C10).
CREATE UNIQUE INDEX IF NOT EXISTS uq_decanate_roster_student_discipline
    ON decanate_roster(student_id, discipline_id);

-- 2. Унікальний частковий індекс за enrollment_id, що унеможливлює повторне внесення одного Web-запису
--    до відомості деканату при повторній обробці або повторних запитах (О1.4 РПО, C10).
CREATE UNIQUE INDEX IF NOT EXISTS uq_decanate_roster_enrollment_id
    ON decanate_roster(enrollment_id)
    WHERE enrollment_id IS NOT NULL;
