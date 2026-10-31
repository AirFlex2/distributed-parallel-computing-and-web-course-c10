-- Міграція 002: Початкові довідкові та тестові дані для спільної теми С10 (Web + РПО)

INSERT INTO discipline (id, code, title, ects, is_test_only) VALUES
    ('DISC-DPC', 'CS-401', 'Розподілені та паралельні обчислення', 6.0, FALSE),
    ('DISC-WEB', 'CS-402', 'Обмін даними у Web-застосунках', 6.0, FALSE),
    ('DISC-AI', 'CS-403', 'Методи штучного інтелекту та машинного навчання', 5.0, FALSE),
    ('DISC-SEC', 'CS-404', 'Кібербезпека хмарних інфраструктур', 5.0, FALSE),
    ('DISC-DB', 'CS-405', 'Високонавантажені бази даних', 4.0, FALSE),
    ('DISC-ECTS-10', 'CS-406', 'Проєктування мікросервісних систем (10.0 ЄКТС)', 10.0, FALSE),
    ('DISC-ECTS-10-5', 'CS-407', 'Квантові алгоритми та криптографія (10.5 ЄКТС)', 10.5, FALSE),
    ('DISC-TEST', 'CS-TEST-O14', 'Ізольований стенд перевірки конкурентності (О1.4 РПО)', 3.0, TRUE)
ON CONFLICT (id) DO UPDATE
SET is_test_only = EXCLUDED.is_test_only;

INSERT INTO discipline_group (
    id, discipline_id, group_code, seats, available,
    opens_at, closes_at, university_tz, closed, allow_overcommit_unsafe
) VALUES
    ('DISC-DPC-01', 'DISC-DPC', 'DPC-G1', 30, 30, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-WEB-01', 'DISC-WEB', 'WEB-G1', 25, 25, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-AI-02', 'DISC-AI', 'AI-G2', 25, 25, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-SEC-03', 'DISC-SEC', 'SEC-G3', 15, 15, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-SEC-04', 'DISC-SEC', 'SEC-G4', 15, 15, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-DB-01', 'DISC-DB', 'DB-G1', 20, 20, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-ONE-SEAT', 'DISC-DB', 'DB-LAST1', 1, 1, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-WINDOW-TEST', 'DISC-WEB', 'WEB-WIN', 20, 20, '2026-09-01T07:00:00Z', '2026-09-15T15:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-ECTS-10-G1', 'DISC-ECTS-10', 'ECTS-10-G1', 20, 20, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-ECTS-10-5-G1', 'DISC-ECTS-10-5', 'ECTS-105-G1', 20, 20, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-TEST-SAFE', 'DISC-TEST', 'TEST-SAFE-G1', 5, 5, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, FALSE),
    ('DISC-TEST-UNSAFE', 'DISC-TEST', 'TEST-UNSAFE-G1', 5, 5, '2025-01-01T08:00:00Z', '2027-12-31T20:00:00Z', 'Europe/Kyiv', FALSE, TRUE)
ON CONFLICT (id) DO UPDATE
SET allow_overcommit_unsafe = EXCLUDED.allow_overcommit_unsafe;

INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects) VALUES
    ('student-1', 'timur.zinkivskyi@inf.edu.ua', 'Зінківський Тимур Євгенійович (ІТІНФ-23-2)', 5, 0, 0.0),
    ('student-2', 'oleksandr.kovalenko@inf.edu.ua', 'Коваленко Олександр Ігорович (ІТІНФ-23-2)', 5, 0, 0.0),
    ('student-3', 'maria.shevchenko@inf.edu.ua', 'Шевченко Марія Василівна (ІТІНФ-23-2)', 5, 0, 0.0),
    ('student-ects-20', 'ects.boundary@inf.edu.ua', 'Тестовий Студент ЄКТС (20.0 активних кредитів)', 5, 2, 20.0),
    ('student-limit-max', 'limit.max@inf.edu.ua', 'Тестовий Студент Ліміту К (5 з 5 дисциплін)', 5, 5, 25.0)
ON CONFLICT (id) DO NOTHING;
