using System.Globalization;

namespace EnrollmentService;

public sealed class ServiceOptions
{
    public string InstanceId { get; set; } = "app-a";
    public string DbConnectionString { get; set; } = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=50";
    public string ApiAuthToken { get; set; } = "secret-c10-token-2026";
    public int QueueCapacity { get; set; } = 100;
    public int Workers { get; set; } = 3;
    public bool UnsafeMode { get; set; } = false;
    public string ExecutionModel { get; set; } = "async_tasks";
    public string QueueEngine { get; set; } = "custom_monitor";
    public int SlowCallMinMs { get; set; } = 200;
    public int SlowCallMaxMs { get; set; } = 700;
    public bool AllowTestOverrides { get; set; } = false;
    public bool EnableWorkers { get; set; } = true;

    // Web ЛР2 (О2.2 та О2.3): TTL ідемпотентних ключів, пакетне очищення та контрольовані відмови (/faults)
    public int IdempotencyKeyTtlMinutes { get; set; } = 1440;
    public int IdempotencyCleanupBatchSize { get; set; } = 100;
    public bool FaultsEnabled { get; set; } = false;
    public string FaultsDirectory { get; set; } = "/faults";

    // РПО ЛР2 (О2.1–О2.4): параметри математичної моделі інтенсивності потоку заявок C10 λ(t) = B + A·exp(-t/τ)
    public double ComputeModelA { get; set; } = 50.0;
    public double ComputeModelB { get; set; } = 5.0;
    public double ComputeModelTau { get; set; } = 10.0;
    public double ComputeModelT { get; set; } = 60.0;
    public int ComputeWorkerTimeoutMs { get; set; } = 60000;

    public static ServiceOptions FromConfiguration(IConfiguration config)
    {
        var options = new ServiceOptions();
        options.InstanceId = config["INSTANCE_ID"] ?? options.InstanceId;
        options.DbConnectionString = config["DB_CONNECTION_STRING"] ?? options.DbConnectionString;
        options.ApiAuthToken = config["API_AUTH_TOKEN"] ?? options.ApiAuthToken;

        if (int.TryParse(config["QUEUE_CAPACITY"], out var qc)) options.QueueCapacity = qc;
        if (int.TryParse(config["WORKERS"], out var w)) options.Workers = w;
        if (bool.TryParse(config["ALLOW_TEST_OVERRIDES"], out var ato)) options.AllowTestOverrides = ato;
        if (bool.TryParse(config["UNSAFE_MODE"], out var um)) options.UnsafeMode = options.AllowTestOverrides && um;

        options.ExecutionModel = config["EXECUTION_MODEL"] ?? options.ExecutionModel;
        options.QueueEngine = config["QUEUE_ENGINE"] ?? options.QueueEngine;

        if (int.TryParse(config["SLOW_CALL_MIN_MS"], out var minMs)) options.SlowCallMinMs = minMs;
        if (int.TryParse(config["SLOW_CALL_MAX_MS"], out var maxMs)) options.SlowCallMaxMs = maxMs;
        if (bool.TryParse(config["ENABLE_WORKERS"], out var ew)) options.EnableWorkers = ew;

        if (int.TryParse(config["IDEMPOTENCY_KEY_TTL_MINUTES"], out var ttlMin) && ttlMin > 0)
            options.IdempotencyKeyTtlMinutes = ttlMin;
        if (int.TryParse(config["IDEMPOTENCY_CLEANUP_BATCH_SIZE"], out var batchSz) && batchSz > 0)
            options.IdempotencyCleanupBatchSize = batchSz;

        var faultsRaw = config["FAULTS"]?.Trim();
        if (!string.IsNullOrEmpty(faultsRaw))
        {
            options.FaultsEnabled = string.Equals(faultsRaw, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(faultsRaw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(faultsRaw, "1", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(config["FAULTS_DIR"]))
            options.FaultsDirectory = config["FAULTS_DIR"]!.Trim();

        if (double.TryParse(config["COMPUTE_MODEL_A"], NumberStyles.Float, CultureInfo.InvariantCulture, out var modelA) && modelA >= 0)
            options.ComputeModelA = modelA;
        if (double.TryParse(config["COMPUTE_MODEL_B"], NumberStyles.Float, CultureInfo.InvariantCulture, out var modelB) && modelB >= 0)
            options.ComputeModelB = modelB;
        if (double.TryParse(config["COMPUTE_MODEL_TAU"], NumberStyles.Float, CultureInfo.InvariantCulture, out var modelTau) && modelTau > 0)
            options.ComputeModelTau = modelTau;
        if (double.TryParse(config["COMPUTE_MODEL_T"], NumberStyles.Float, CultureInfo.InvariantCulture, out var modelT) && modelT > 0)
            options.ComputeModelT = modelT;
        if (int.TryParse(config["COMPUTE_WORKER_TIMEOUT_MS"], out var workerTimeout) && workerTimeout >= 1000)
            options.ComputeWorkerTimeoutMs = workerTimeout;

        bool isProduction = string.Equals(config["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config["DOTNET_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);

        if (isProduction && (string.IsNullOrWhiteSpace(options.ApiAuthToken) || string.Equals(options.ApiAuthToken, "secret-c10-token-2026", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "[CRITICAL SECURITY ERROR]: Запуск у виробничому середовищі (Production) зі стандартним або порожнім API_AUTH_TOKEN заборонено. " +
                "Необхідно задати унікальний криптографічно стійкий секрет через конфігурацію виробничого оточення (змінна API_AUTH_TOKEN).");
        }

        return options;
    }
}
