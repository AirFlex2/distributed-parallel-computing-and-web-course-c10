using System.Text.Json.Serialization;

namespace EnrollmentService;

public sealed class CreateEnrollmentRequestDto
{
    [JsonPropertyName("student_id")]
    public string? StudentId { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    [JsonPropertyName("request_id")]
    public Guid? RequestId { get; set; }

    [JsonPropertyName("enrollment_id")]
    public Guid? EnrollmentId { get; set; }
}

public sealed record EnrollmentAcceptedResponseDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt
);

public sealed record EnrollmentStatusResponseDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("first_read_at")] DateTimeOffset? FirstReadAt,
    [property: JsonPropertyName("queue_wait_ms")] double? QueueWaitMs,
    [property: JsonPropertyName("processing_time_ms")] double? ProcessingTimeMs,
    [property: JsonPropertyName("delivery_wait_ms")] double? DeliveryWaitMs,
    [property: JsonPropertyName("assigned_group_id")] string? AssignedGroupId = null,
    [property: JsonPropertyName("recorded_in_roster")] bool RecordedInRoster = false
);

public sealed record GroupSeatsResponseDto(
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("discipline_title")] string DisciplineTitle,
    [property: JsonPropertyName("seats")] int Seats,
    [property: JsonPropertyName("taken")] int Taken,
    [property: JsonPropertyName("free_seats")] int FreeSeats,
    [property: JsonPropertyName("invariant_holds")] bool InvariantHolds,
    [property: JsonPropertyName("roster_count")] int RosterCount = 0
)
{
    [JsonPropertyName("invariant_ok")]
    public bool InvariantOk => InvariantHolds;

    [JsonPropertyName("capacity")]
    public int Capacity => Seats;

    [JsonPropertyName("used")]
    public int Used => Taken;
}

public sealed record DecanateRosterEntryDto(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("discipline_id")] string DisciplineId,
    [property: JsonPropertyName("discipline_title")] string DisciplineTitle,
    [property: JsonPropertyName("ects")] decimal Ects,
    [property: JsonPropertyName("enrollment_id")] string? EnrollmentId,
    [property: JsonPropertyName("assignment_type")] string AssignmentType,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt
);

public sealed class QueuedRequestItem
{
    public required Guid Id { get; init; }
    public required string StudentId { get; init; }
    public required string GroupId { get; init; }
    public Guid? EnrollmentId { get; init; }
    public Guid? RequestedEnrollmentId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? RequestPayloadHash { get; init; }
    public long EnqueueTimestamp;
    public long StartProcessingTimestamp;
    public long FinishProcessingTimestamp;
    public long FirstReadTimestamp;
    public int DeliverySampleRecorded;
    public bool IsRecoveredFromDb { get; init; }
    public double RecoveredQueueWaitMs;
}
