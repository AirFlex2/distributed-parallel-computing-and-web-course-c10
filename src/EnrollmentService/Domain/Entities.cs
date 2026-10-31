namespace EnrollmentService.Domain;

public sealed record StudentEntity(
    string Id,
    string Email,
    string FullName,
    int MaxDisciplines,
    int ActiveCount,
    decimal ActiveEcts
);

public sealed record DisciplineGroupEntity(
    string Id,
    string DisciplineId,
    string GroupCode,
    int Seats,
    int Available,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    string UniversityTz,
    bool Closed,
    bool AllowOvercommitUnsafe,
    decimal DisciplineEcts = 0.0m
);

public sealed record DisciplineEntity(
    string Id,
    string Code,
    string Title,
    decimal Ects
);

public sealed record GroupSeatEntity(
    string GroupId,
    string DisciplineTitle,
    int Seats,
    int Taken,
    int RosterCount
);
