using System.Text.Json.Serialization;

namespace EnrollmentService;

// ============================================================================
// МОДЕЛІ ДЛЯ КУРСУ «РОЗПОДІЛЕНІ ТА ПАРАЛЕЛЬНІ ОБЧИСЛЕННЯ» (О1.1 – О1.5)
// ============================================================================

public sealed class CreateEnrollmentRequestDto
{
    [JsonPropertyName("studentId")]
    public string? StudentId { get; set; }

    [JsonPropertyName("student_id")]
    public string? StudentIdSnake
    {
        get => StudentId;
        set { if (StudentId == null) StudentId = value; }
    }

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupIdSnake
    {
        get => GroupId;
        set { if (GroupId == null) GroupId = value; }
    }

    [JsonPropertyName("requestId")]
    public Guid? RequestId { get; set; }

    [JsonPropertyName("request_id")]
    public Guid? RequestIdSnake
    {
        get => RequestId;
        set { if (RequestId == null) RequestId = value; }
    }

    [JsonPropertyName("secondChoiceGroupId")]
    public string? SecondChoiceGroupId { get; set; }

    [JsonPropertyName("second_choice_group_id")]
    public string? SecondChoiceGroupIdSnake
    {
        get => SecondChoiceGroupId;
        set { if (SecondChoiceGroupId == null) SecondChoiceGroupId = value; }
    }
}

public sealed record EnrollmentAcceptedResponseDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("secondChoiceGroupId")] string? SecondChoiceGroupId = null
)
{
    [JsonPropertyName("student_id")] public string StudentIdSnake => StudentId;
    [JsonPropertyName("group_id")] public string GroupIdSnake => GroupId;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAtSnake => CreatedAt;
    [JsonPropertyName("second_choice_group_id")] public string? SecondChoiceGroupIdSnake => SecondChoiceGroupId;
}

public sealed record EnrollmentStatusResponseDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("studentId")] string StudentId,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("firstReadAt")] DateTimeOffset? FirstReadAt,
    [property: JsonPropertyName("queueWaitMs")] double? QueueWaitMs,
    [property: JsonPropertyName("processingTimeMs")] double? ProcessingTimeMs,
    [property: JsonPropertyName("deliveryWaitMs")] double? DeliveryWaitMs,
    [property: JsonPropertyName("secondChoiceGroupId")] string? SecondChoiceGroupId = null,
    [property: JsonPropertyName("assignedGroupId")] string? AssignedGroupId = null,
    [property: JsonPropertyName("recordedInRoster")] bool RecordedInRoster = false
)
{
    [JsonPropertyName("student_id")] public string StudentIdSnake => StudentId;
    [JsonPropertyName("group_id")] public string GroupIdSnake => GroupId;
    [JsonPropertyName("rejection_reason")] public string? RejectionReasonSnake => RejectionReason;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAtSnake => CreatedAt;
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAtSnake => StartedAt;
    [JsonPropertyName("finished_at")] public DateTimeOffset? FinishedAtSnake => FinishedAt;
    [JsonPropertyName("first_read_at")] public DateTimeOffset? FirstReadAtSnake => FirstReadAt;
    [JsonPropertyName("queue_wait_ms")] public double? QueueWaitMsSnake => QueueWaitMs;
    [JsonPropertyName("processing_time_ms")] public double? ProcessingTimeMsSnake => ProcessingTimeMs;
    [JsonPropertyName("delivery_wait_ms")] public double? DeliveryWaitMsSnake => DeliveryWaitMs;
    [JsonPropertyName("second_choice_group_id")] public string? SecondChoiceGroupIdSnake => SecondChoiceGroupId;
    [JsonPropertyName("assigned_group_id")] public string? AssignedGroupIdSnake => AssignedGroupId;
    [JsonPropertyName("recorded_in_roster")] public bool RecordedInRosterSnake => RecordedInRoster;
}

public sealed record GroupSeatsResponseDto(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("disciplineTitle")] string DisciplineTitle,
    [property: JsonPropertyName("seats")] int Seats,
    [property: JsonPropertyName("taken")] int Taken,
    [property: JsonPropertyName("freeSeats")] int FreeSeats,
    [property: JsonPropertyName("invariantHolds")] bool InvariantHolds,
    [property: JsonPropertyName("rosterCount")] int RosterCount = 0
)
{
    [JsonPropertyName("group_id")] public string GroupIdSnake => GroupId;
    [JsonPropertyName("discipline_title")] public string DisciplineTitleSnake => DisciplineTitle;
    [JsonPropertyName("free_seats")] public int FreeSeatsSnake => FreeSeats;
    [JsonPropertyName("invariant_holds")] public bool InvariantHoldsSnake => InvariantHolds;
    [JsonPropertyName("roster_count")] public int RosterCountSnake => RosterCount;
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
)
{
    [JsonPropertyName("requestId")] public Guid RequestIdCamel => RequestId;
    [JsonPropertyName("studentId")] public string StudentIdCamel => StudentId;
    [JsonPropertyName("groupId")] public string GroupIdCamel => GroupId;
    [JsonPropertyName("disciplineId")] public string DisciplineIdCamel => DisciplineId;
    [JsonPropertyName("disciplineTitle")] public string DisciplineTitleCamel => DisciplineTitle;
    [JsonPropertyName("enrollmentId")] public string? EnrollmentIdCamel => EnrollmentId;
    [JsonPropertyName("assignmentType")] public string AssignmentTypeCamel => AssignmentType;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAtCamel => CreatedAt;
}

public sealed class SecondChoiceDistributionRequestDto
{
    [JsonPropertyName("student_id")]
    public string? StudentId { get; set; }

    [JsonPropertyName("studentId")]
    public string? StudentIdCamel
    {
        get => StudentId;
        set { if (StudentId == null) StudentId = value; }
    }

    [JsonPropertyName("second_choice_group_id")]
    public string? SecondChoiceGroupId { get; set; }

    [JsonPropertyName("secondChoiceGroupId")]
    public string? SecondChoiceGroupIdCamel
    {
        get => SecondChoiceGroupId;
        set { if (SecondChoiceGroupId == null) SecondChoiceGroupId = value; }
    }

    [JsonPropertyName("request_id")]
    public Guid? RequestId { get; set; }

    [JsonPropertyName("requestId")]
    public Guid? RequestIdCamel
    {
        get => RequestId;
        set { if (RequestId == null) RequestId = value; }
    }
}

public sealed record SecondChoiceDistributionItemDto(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("primary_group_id")] string PrimaryGroupId,
    [property: JsonPropertyName("second_choice_group_id")] string SecondChoiceGroupId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("enrollment_id")] string? EnrollmentId,
    [property: JsonPropertyName("recorded_in_roster")] bool RecordedInRoster,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason
)
{
    [JsonPropertyName("requestId")] public Guid RequestIdCamel => RequestId;
    [JsonPropertyName("studentId")] public string StudentIdCamel => StudentId;
    [JsonPropertyName("primaryGroupId")] public string PrimaryGroupIdCamel => PrimaryGroupId;
    [JsonPropertyName("secondChoiceGroupId")] public string SecondChoiceGroupIdCamel => SecondChoiceGroupId;
    [JsonPropertyName("enrollmentId")] public string? EnrollmentIdCamel => EnrollmentId;
    [JsonPropertyName("recordedInRoster")] public bool RecordedInRosterCamel => RecordedInRoster;
    [JsonPropertyName("rejectionReason")] public string? RejectionReasonCamel => RejectionReason;
}

public sealed record SecondChoiceDistributionResponseDto(
    [property: JsonPropertyName("primary_group_id")] string PrimaryGroupId,
    [property: JsonPropertyName("primary_group_closed")] bool PrimaryGroupClosed,
    [property: JsonPropertyName("distributed_count")] int DistributedCount,
    [property: JsonPropertyName("rejected_count")] int RejectedCount,
    [property: JsonPropertyName("items")] IReadOnlyList<SecondChoiceDistributionItemDto> Items
)
{
    [JsonPropertyName("primaryGroupId")] public string PrimaryGroupIdCamel => PrimaryGroupId;
    [JsonPropertyName("primaryGroupClosed")] public bool PrimaryGroupClosedCamel => PrimaryGroupClosed;
    [JsonPropertyName("distributedCount")] public int DistributedCountCamel => DistributedCount;
    [JsonPropertyName("rejectedCount")] public int RejectedCountCamel => RejectedCount;
}

public sealed class QueuedRequestItem
{
    public required Guid Id { get; init; }
    public required string StudentId { get; init; }
    public required string GroupId { get; init; }
    public string? SecondChoiceGroupId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public long EnqueueTimestamp;
    public long StartProcessingTimestamp;
    public long FinishProcessingTimestamp;
    public long FirstReadTimestamp;
    public int DeliverySampleRecorded;
}
