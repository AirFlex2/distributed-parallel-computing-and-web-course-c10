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

        bool isProduction = string.Equals(config["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config["DOTNET_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);

        if (isProduction && string.Equals(options.ApiAuthToken, "secret-c10-token-2026", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("[SECURITY WARNING]: У виробничому середовищі (Production) використовується стандартний навчальний API_AUTH_TOKEN. Необхідно задати виділений секрет через конфігурацію виробничого оточення.");
        }

        return options;
    }
}
