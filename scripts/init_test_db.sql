-- Автоматичне створення виділеної тестової бази даних під час первинної ініціалізації контейнера PostgreSQL
SELECT 'CREATE DATABASE dpc_c10_test OWNER dpc_user'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'dpc_c10_test')\gexec
