using System.Text.Json;

namespace EnrollmentService.Http;

public static class HttpPayloadHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<(T? Dto, IResult? ErrorResult)> ReadAndValidateAsync<T>(
        HttpContext context,
        ServiceOptions opts) where T : class
    {
        using var reader = new StreamReader(context.Request.Body);
        string rawBody = await reader.ReadToEndAsync(context.RequestAborted);

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return (null, ProblemDetailsMapper.CreateProblemResult(
                context, opts,
                StatusCodes.Status400BadRequest,
                "Порожнє тіло запиту",
                "Тіло HTTP-запиту є порожнім. Очікується JSON-об'єкт.",
                "empty_request_body"));
        }

        // 1. Перевірка синтаксичної коректності JSON (Синтаксична помилка -> 400 Bad Request)
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний формат JSON",
                    "Очікується коректний кореневий JSON-об'єкт.",
                    "invalid_json_syntax"));
            }
        }
        catch (JsonException ex)
        {
            return (null, ProblemDetailsMapper.CreateProblemResult(
                context, opts,
                StatusCodes.Status400BadRequest,
                "Синтаксична помилка JSON",
                $"Неможливо розібрати синтаксис JSON: {ex.Message}",
                "invalid_json_syntax"));
        }

        // 2. Десеріалізація та валідація типів полів (Помилка типу або структури -> 422 Unprocessable Entity)
        T? dto;
        try
        {
            dto = JsonSerializer.Deserialize<T>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, ProblemDetailsMapper.CreateProblemResult(
                context, opts,
                StatusCodes.Status422UnprocessableEntity,
                "Некоректний тип поля у JSON",
                $"Значення поля не відповідає очікуваному типу схеми: {ex.Message}",
                "invalid_field_type"));
        }

        if (dto == null)
        {
            return (null, ProblemDetailsMapper.CreateProblemResult(
                context, opts,
                StatusCodes.Status422UnprocessableEntity,
                "Некоректний формат даних",
                "JSON розібрано як порожній об'єкт.",
                "invalid_field_type"));
        }

        // Семантична валідація обов'язкових полів: 422 Unprocessable Content
        if (dto is CreateEnrollmentRequestDto dpcDto)
        {
            if (string.IsNullOrWhiteSpace(dpcDto.StudentId) || string.IsNullOrWhiteSpace(dpcDto.GroupId))
            {
                return (null, ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status422UnprocessableEntity,
                    "Помилка валідації полів запиту",
                    "Поля student_id та group_id є обов'язковими для створення заявки.",
                    "missing_required_fields"));
            }
        }
        else if (dto is CreateWebEnrollmentDto webDto)
        {
            if (string.IsNullOrWhiteSpace(webDto.GroupId))
            {
                return (null, ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status422UnprocessableEntity,
                    "Помилка валідації полів запиту",
                    "Поле group_id є обов'язковим для запису на дисципліну.",
                    "missing_required_fields"));
            }

            if (!string.IsNullOrWhiteSpace(webDto.StudentId))
            {
                return (null, ProblemDetailsMapper.CreateProblemResult(
                    context, opts,
                    StatusCodes.Status422UnprocessableEntity,
                    "Заборонено передавати student_id у тілі запиту",
                    "Ідентифікатор студента визначається виключно із заголовка X-User-Id для запобігання підміні авторства.",
                    "owner_in_body_forbidden"));
            }
        }

        return (dto, null);
    }
}
