using EnrollmentService.Services;

namespace EnrollmentService.Http;

// П. 9 (Д1.4): Єдине проміжне ПЗ обробки SIGTERM / Graceful Shutdown для Web та РПО маршрутів
public sealed class GracefulShutdownMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServiceOptions _options;
    private readonly EnrollmentWorkerPool _workerPool;

    public GracefulShutdownMiddleware(
        RequestDelegate next,
        ServiceOptions options,
        EnrollmentWorkerPool workerPool)
    {
        _next = next;
        _options = options;
        _workerPool = workerPool;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_workerPool.IsShuttingDown
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method))
        {
            await ProblemDetailsMapper.WriteProblemResponseAsync(
                context, _options,
                StatusCodes.Status503ServiceUnavailable,
                "Сервіс завершує роботу",
                "Вузол отримав сигнал SIGTERM (Д1.4). Нові змінюючі запити відхиляються з HTTP 503, триває завершення поточної черги.",
                "service_shutting_down");
            return;
        }

        await _next(context);
    }
}
