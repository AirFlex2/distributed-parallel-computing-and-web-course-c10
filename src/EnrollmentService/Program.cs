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

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
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

// Конвеєр проміжного ПЗ (Middleware)
app.UseCors();
app.UseMiddleware<RequestIdentityMiddleware>();
app.UseMiddleware<BearerAuthMiddleware>();

// Реєстрація модульних HTTP-маршрутів
app.MapDiagnosticsEndpoints();
app.MapEnrollmentEndpoints();
app.MapEnrollmentRequestEndpoints();
app.MapGroupEndpoints();
app.MapAdminEndpoints();

app.Run();
