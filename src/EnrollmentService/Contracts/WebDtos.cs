using System.Text.Json.Serialization;

namespace EnrollmentService;

// ============================================================================
// МОДЕЛІ ДЛЯ КУРСУ «ОБМІН ДАНИМИ У WEB-ЗАСТОСУНКАХ» (О1.3, О1.4, О1.5, Д1.5)
// Усі ідентифікатори у відповідях передаються як рядки (string)
// ============================================================================

public sealed class CreateWebEnrollmentDto
{
    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    [JsonPropertyName("student_id")]
    public string? StudentId { get; set; }
}

public sealed record WebEnrollmentResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("discipline_id")] string DisciplineId,
    [property: JsonPropertyName("discipline_title")] string DisciplineTitle,
    [property: JsonPropertyName("ects")] decimal Ects,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("created_at_local")] string CreatedAtLocal,
    [property: JsonPropertyName("expires_at_local")] string ExpiresAtLocal,
    [property: JsonPropertyName("university_tz")] string UniversityTz,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("extra_server_field")] string ExtraServerField = "forward_compatible_o1_7"
);

public sealed record WebEnrollmentListResponseDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WebEnrollmentResponseDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("active_count")] int ActiveCount,
    [property: JsonPropertyName("max_disciplines")] int MaxDisciplines,
    [property: JsonPropertyName("active_ects")] decimal ActiveEcts,
    [property: JsonPropertyName("max_ects")] decimal MaxEcts
);

public sealed record WebDisciplineGroupDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("discipline_id")] string DisciplineId,
    [property: JsonPropertyName("group_code")] string GroupCode,
    [property: JsonPropertyName("seats")] int Seats,
    [property: JsonPropertyName("available")] int Available,
    [property: JsonPropertyName("opens_at")] DateTimeOffset OpensAt,
    [property: JsonPropertyName("closes_at")] DateTimeOffset ClosesAt,
    [property: JsonPropertyName("opens_at_local")] string OpensAtLocal,
    [property: JsonPropertyName("closes_at_local")] string ClosesAtLocal,
    [property: JsonPropertyName("university_tz")] string UniversityTz,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("window_active")] bool WindowActive
);

public sealed record WebDisciplineDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("ects")] decimal Ects,
    [property: JsonPropertyName("groups")] IReadOnlyList<WebDisciplineGroupDto> Groups
);

public sealed record WebDisciplineListResponseDto(
    [property: JsonPropertyName("items")] IReadOnlyList<WebDisciplineDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor
);

public sealed record WebStudentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("max_disciplines")] int MaxDisciplines,
    [property: JsonPropertyName("active_count")] int ActiveCount,
    [property: JsonPropertyName("active_ects")] decimal ActiveEcts,
    [property: JsonPropertyName("max_ects")] decimal MaxEcts
);
