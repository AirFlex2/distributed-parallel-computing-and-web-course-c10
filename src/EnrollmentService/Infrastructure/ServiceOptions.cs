namespace EnrollmentService;

// Конфігурація сервісу з підтримкою змінних середовища для основних та додаткових пунктів ЛР1 (Web + РПО)
public sealed class ServiceOptions
{
    public string InstanceId { get; set; } = "app-a"; // О1.6 (Web): Ім'я екземпляра додатка (app-a або app-b)
    public string DbConnectionString { get; set; } = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=50";
    public string ApiAuthToken { get; set; } = "secret-c10-token-2026";
    public int QueueCapacity { get; set; } = 100; // О1.2 (РПО): Місткість обмеженої черги
    public int Workers { get; set; } = 3; // О1.3 (РПО): Кількість обробників у пулі
    public bool UnsafeMode { get; set; } = false; // О1.4 (РПО): Прапорець небезпечного режиму без блокувань
    public string ExecutionModel { get; set; } = "async_tasks"; // Д1.1 (РПО): async_tasks або dedicated_threads
    public string QueueEngine { get; set; } = "custom_monitor"; // Д1.3 (РПО): custom_monitor або system_channel
    public int SlowCallMinMs { get; set; } = 200; // О1.3 (РПО): Мінімальна затримка деканату
    public int SlowCallMaxMs { get; set; } = 700; // О1.3 (РПО): Максимальна затримка деканату
    public bool AllowTestOverrides { get; set; } = false; // Захист тестових хуків (evaluated_at, X-Simulated-Now, /v1/admin/reset, UNSAFE_MODE)

    public static ServiceOptions FromConfiguration(IConfiguration config)
    {
        bool allowOverrides = bool.TryParse(
            Environment.GetEnvironmentVariable("ALLOW_TEST_OVERRIDES") ?? config["ALLOW_TEST_OVERRIDES"],
            out var ato) && ato;

        bool requestedUnsafe = bool.TryParse(
            Environment.GetEnvironmentVariable("UNSAFE_MODE") ?? config["UNSAFE_MODE"],
            out var um) && um;

        return new ServiceOptions
        {
            InstanceId = Environment.GetEnvironmentVariable("INSTANCE_ID")
                ?? config["INSTANCE_ID"]
                ?? "app-a",
            DbConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
                ?? config["DB_CONNECTION_STRING"]
                ?? "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=50",
            ApiAuthToken = Environment.GetEnvironmentVariable("API_AUTH_TOKEN")
                ?? config["API_AUTH_TOKEN"]
                ?? "secret-c10-token-2026",
            QueueCapacity = int.TryParse(Environment.GetEnvironmentVariable("QUEUE_CAPACITY") ?? config["QUEUE_CAPACITY"], out var qc) ? qc : 100,
            Workers = int.TryParse(Environment.GetEnvironmentVariable("WORKERS") ?? config["WORKERS"], out var w) ? w : 3,
            AllowTestOverrides = allowOverrides,
            UnsafeMode = allowOverrides && requestedUnsafe,
            ExecutionModel = Environment.GetEnvironmentVariable("EXECUTION_MODEL") ?? config["EXECUTION_MODEL"] ?? "async_tasks",
            QueueEngine = Environment.GetEnvironmentVariable("QUEUE_ENGINE") ?? config["QUEUE_ENGINE"] ?? "custom_monitor",
            SlowCallMinMs = int.TryParse(Environment.GetEnvironmentVariable("SLOW_CALL_MIN_MS") ?? config["SLOW_CALL_MIN_MS"], out var minMs) ? minMs : 200,
            SlowCallMaxMs = int.TryParse(Environment.GetEnvironmentVariable("SLOW_CALL_MAX_MS") ?? config["SLOW_CALL_MAX_MS"], out var maxMs) ? maxMs : 700
        };
    }
}
