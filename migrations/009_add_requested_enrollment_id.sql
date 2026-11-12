-- Міграція 009: Додавання колонки requested_enrollment_id до таблиці enrollment_requests
-- для надійного збереження вихідного ідентифікатора Web-запису без зовнішнього ключа.
-- Це гарантує збереження вихідного зв'язку навіть при видаленні Web-запису (ON DELETE SET NULL)
-- та дозволяє воркеру виявляти передачу неіснуючих або видалених записів.

ALTER TABLE enrollment_requests
    ADD COLUMN IF NOT EXISTS requested_enrollment_id UUID NULL;

-- Для вже існуючих записів синхронізуємо значення з enrollment_id
UPDATE enrollment_requests
SET requested_enrollment_id = enrollment_id
WHERE requested_enrollment_id IS NULL AND enrollment_id IS NOT NULL;
