using System.Runtime.InteropServices;
using EnrollmentService;
using EnrollmentService.Endpoints;
using EnrollmentService.Extensions;
using EnrollmentService.Http;
using EnrollmentService.Services;

var builder = WebApplication.CreateBuilder(args);

// Прибираємо стандартний багаторядковий текстовий логер ASP.NET Core:
// усі HTTP-запити та події життєвого циклу (міграції, старт воркерів, SIGTERM) пишуться
// через StructuredJsonLogger рівно одним рядком JSON у stdout (О1.6 Web, Д1.4 РПО).
builder.Logging.ClearProviders();

builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(55);
});

bool migrateOnly = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase);

builder.Services
    .AddInfrastructureServices(builder.Configuration, out var serviceOptions)
    .AddRepositories()
    .AddApplicationServices()
    .AddWorkerProcessing(migrateOnly)
    .AddObservability();

// П. 3 (Д1.4): Замінюємо стандартний ConsoleLifetime на no-op реалізацію, щоб Kestrel
// НЕ зупинявся завчасно при SIGTERM і не спричиняв 502 у балансувальника Nginx.
// Сигнали обробляються нашим власним PosixSignalRegistration, нові запити отримують 503,
// а StopApplication() викликається тільки після повного спустошення черги воркерів.
builder.Services.AddSingleton<IHostLifetime, DisabledConsoleLifetime>();

// П. 13 (Web О3.3): Явна заборона AllowAnyOrigin (*); дозволено виключно origin клієнта
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("X-Request-ID", "X-Instance-Id", "X-Trace-Id", "Location", "Retry-After"));
});

var app = builder.Build();

// Застосування версіонованих SQL-міграцій із таблицею schema_migrations
using (var scope = app.Services.CreateScope())
{
    var dbInit = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await dbInit.InitializeAsync();
}

if (migrateOnly)
{
    return;
}

// П. 3 (Д1.4): Перехоплення SIGTERM/SIGINT через PosixSignalRegistration.
// Kestrel НЕ припиняє роботу завчасно, а продовжує приймати HTTP-з'єднання і відповідати 503,
// поки воркери повністю дообробляють прийняту чергу, після чого викликається StopApplication().
var workerPool = app.Services.GetRequiredService<EnrollmentWorkerPool>();
var signalRegistrations = new List<IDisposable>();
try
{
    signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        workerPool.InitiateShutdown();
    }));
    signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
    {
        ctx.Cancel = true;
        workerPool.InitiateShutdown();
    }));
}
catch (PlatformNotSupportedException)
{
    // Fallback для платформ без підтримки POSIX-сигналів
}

// Конвеєр проміжного ПЗ (Middleware)
// 1. RequestIdentityMiddleware стоїть найпершим: фіксує X-Request-ID, X-Trace-Id та записує
//    рівно один JSON-рядок логу для абсолютно КОЖНОГО запиту, включно з CORS preflight OPTIONS (О1.6).
app.UseMiddleware<RequestIdentityMiddleware>();

// 2. UseCors стоїть після RequestIdentityMiddleware, обробляючи preflight OPTIONS-запити
//    та проставляючи заголовки Access-Control-* (О1.6, О3.3).
app.UseCors();

// 3. Обробка статусних кодів без тіла відповіді (невідомий шлях 404, непідтримуваний метод 405)
//    у форматі RFC 9457 Problem Details (О1.3).
app.UseStatusCodePages(async scContext =>
{
    var context = scContext.HttpContext;
    var opts = context.RequestServices.GetRequiredService<ServiceOptions>();
    int status = context.Response.StatusCode;

    if (status == StatusCodes.Status404NotFound)
    {
        await ProblemDetailsMapper.WriteProblemResponseAsync(
            context, opts,
            StatusCodes.Status404NotFound,
            "Маршрут не знайдено",
            $"Запитуваний шлях '{context.Request.Path}' не існує на сервері.",
            "route_not_found");
    }
    else if (status == StatusCodes.Status405MethodNotAllowed)
    {
        await ProblemDetailsMapper.WriteProblemResponseAsync(
            context, opts,
            StatusCodes.Status405MethodNotAllowed,
            "HTTP-метод не підтримується",
            $"HTTP-метод '{context.Request.Method}' не дозволений для шляху '{context.Request.Path}'.",
            "method_not_allowed");
    }
});

app.UseMiddleware<GracefulShutdownMiddleware>();
app.UseMiddleware<BearerAuthMiddleware>();

// Реєстрація модульних HTTP-маршрутів
app.MapDiagnosticsEndpoints();
app.MapEnrollmentEndpoints();
app.MapEnrollmentRequestEndpoints();
app.MapGroupEndpoints();
app.MapAdminEndpoints();

app.Run();

GC.KeepAlive(signalRegistrations);
foreach (var reg in signalRegistrations)
{
    reg.Dispose();
}

public sealed class DisabledConsoleLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
