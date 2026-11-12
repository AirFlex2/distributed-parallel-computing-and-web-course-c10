using System.Text.Json;
using EnrollmentService.Http;
using EnrollmentService.Services.Compute;

namespace EnrollmentService.Endpoints;

public static class ComputeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapComputeEndpoints(this WebApplication app)
    {
        // РПО ЛР2 (О2.1–О2.4): Синхронне паралельне чисельне інтегрування функції інтенсивності потоку заявок C10
        app.MapPost("/v1/compute", async (
            HttpContext context,
            ServiceOptions opts,
            ParallelComputeService computeService) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string rawBody = await reader.ReadToEndAsync(context.RequestAborted);

            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status400BadRequest,
                    "Порожнє тіло запиту",
                    "Тіло HTTP-запиту є порожнім. Очікується JSON-об'єкт із параметрами обчислення (a, b, n, workers).",
                    "empty_request_body");
            }

            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        context, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний формат JSON",
                        "Очікується кореневий JSON-об'єкт.",
                        "invalid_json_syntax");
                }
            }
            catch (JsonException ex)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status400BadRequest,
                    "Синтаксична помилка JSON",
                    $"Неможливо розібрати синтаксис JSON: {ex.Message}",
                    "invalid_json_syntax");
            }

            ComputeRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ComputeRequestDto>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний тип параметра у JSON",
                    $"Значення параметра обчислення не відповідає очікуваному типу: {ex.Message}",
                    "invalid_compute_parameter_type");
            }

            if (dto == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний формат даних",
                    "JSON розібрано як порожній об'єкт.",
                    "invalid_compute_parameters");
            }

            var result = await computeService.ComputeAsync(dto, context.RequestAborted);
            if (!result.IsSuccess || result.Value == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    result.StatusCode,
                    result.ErrorTitle ?? "Помилка обчислення",
                    result.ErrorDetail ?? "Не вдалося виконати паралельне чисельне інтегрування.",
                    result.ErrorCode ?? "compute_error");
            }

            return Results.Ok(result.Value);
        });
    }
}
