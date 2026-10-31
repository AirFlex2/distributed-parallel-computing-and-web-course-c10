using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace EnrollmentService.Services;

// Версіонований застосовувач SQL-міграцій із таблицею schema_migrations та advisory-блокуванням PostgreSQL (О1.2 Web та О1.1 РПО)
public sealed class DatabaseInitializer
{
    private const long MigrationAdvisoryLockKey = 20261010L;
    private readonly ServiceOptions _options;

    public DatabaseInitializer(ServiceOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string[] candidateDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "migrations"),
            Path.Combine(Directory.GetCurrentDirectory(), "migrations"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "migrations")
        ];

        string? migrationDir = candidateDirs.FirstOrDefault(Directory.Exists);
        if (migrationDir == null)
        {
            throw new DirectoryNotFoundException("Каталог міграцій 'migrations/' не знайдено.");
        }

        string[] migrationFiles = Directory.GetFiles(migrationDir, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        if (migrationFiles.Length == 0)
        {
            throw new FileNotFoundException($"У каталозі '{migrationDir}' не знайдено жодного SQL-файлу міграції.");
        }

        for (int attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_options.DbConnectionString);
                await conn.OpenAsync(cancellationToken);

                int appliedNow = 0;

                await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
                {
                    // Ексклюзивне транзакційне блокування, щоб app-a та app-b при одночасному старті не конфліктували
                    await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", conn, tx))
                    {
                        lockCmd.Parameters.AddWithValue("key", MigrationAdvisoryLockKey);
                        await lockCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var createTableCmd = new NpgsqlCommand(@"
                        CREATE TABLE IF NOT EXISTS schema_migrations (
                            version TEXT PRIMARY KEY,
                            filename TEXT NOT NULL,
                            checksum TEXT NOT NULL,
                            applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                        );", conn, tx))
                    {
                        await createTableCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    var existingVersions = new Dictionary<string, string>(StringComparer.Ordinal);
                    await using (var readCmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations;", conn, tx))
                    await using (var reader = await readCmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            existingVersions[reader.GetString(0)] = reader.GetString(1);
                        }
                    }

                    foreach (string filePath in migrationFiles)
                    {
                        string fileName = Path.GetFileName(filePath);
                        string version = fileName.Split('_', 2)[0];
                        string sql = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
                        string checksum = ComputeSha256Hex(sql);

                        if (existingVersions.TryGetValue(version, out string? appliedChecksum))
                        {
                            if (string.Equals(appliedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            await using (var reapplyCmd = new NpgsqlCommand(sql, conn, tx))
                            {
                                await reapplyCmd.ExecuteNonQueryAsync(cancellationToken);
                            }

                            await using (var updateChecksumCmd = new NpgsqlCommand(@"
                                UPDATE schema_migrations
                                SET checksum = @checksum, applied_at = NOW()
                                WHERE version = @version;", conn, tx))
                            {
                                updateChecksumCmd.Parameters.AddWithValue("version", version);
                                updateChecksumCmd.Parameters.AddWithValue("checksum", checksum);
                                await updateChecksumCmd.ExecuteNonQueryAsync(cancellationToken);
                            }

                            appliedNow++;
                            StructuredJsonLogger.LogEvent(_options.InstanceId, "migration_updated", new Dictionary<string, object?>
                            {
                                ["version"] = version,
                                ["filename"] = fileName,
                                ["checksum"] = checksum
                            });
                            continue;
                        }

                        await using (var applyCmd = new NpgsqlCommand(sql, conn, tx))
                        {
                            await applyCmd.ExecuteNonQueryAsync(cancellationToken);
                        }

                        await using (var recordCmd = new NpgsqlCommand(@"
                            INSERT INTO schema_migrations (version, filename, checksum, applied_at)
                            VALUES (@version, @filename, @checksum, NOW());", conn, tx))
                        {
                            recordCmd.Parameters.AddWithValue("version", version);
                            recordCmd.Parameters.AddWithValue("filename", fileName);
                            recordCmd.Parameters.AddWithValue("checksum", checksum);
                            await recordCmd.ExecuteNonQueryAsync(cancellationToken);
                        }

                        appliedNow++;
                        StructuredJsonLogger.LogEvent(_options.InstanceId, "migration_applied", new Dictionary<string, object?>
                        {
                            ["version"] = version,
                            ["filename"] = fileName,
                            ["checksum"] = checksum
                        });
                    }

                    await tx.CommitAsync(cancellationToken);
                }

                StructuredJsonLogger.LogEvent(_options.InstanceId, "database_ready", new Dictionary<string, object?>
                {
                    ["total_migrations"] = migrationFiles.Length,
                    ["applied_now"] = appliedNow
                });
                return;
            }
            catch (Exception ex) when (attempt < 12)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "database_retry", new Dictionary<string, object?>
                {
                    ["attempt"] = attempt,
                    ["error"] = ex.Message
                });
                await Task.Delay(2000, cancellationToken);
            }
        }
    }

    private static string ComputeSha256Hex(string content)
    {
        string normalized = content.Replace("\r\n", "\n");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
