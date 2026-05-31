namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public record AppointmentDto(
    Guid AppointmentId,
    string? DisplayId,
    Guid PatientId,
    string? PatientName,
    string? Mobile,
    string? PatientAge,
    string? PatientGender,
    string? PatientIdentifier,
    string? Service,
    string? Modality,
    DateTime DateTime,
    string? Type,
    string? Doctor,
    string? Status,
    string? ReferredBy,
    string? ReferredContact,
    string? Notes,
    string? TechnicianComments,
    Guid? TechnicianId,
    DateTime? ScannedAt,
    decimal Amount = 0,
    decimal ReferralCutValue = 0,
    int AssetCount = 0,
    string? ReportImpression = null,
    int? DailyTokenNumber = null,
    string? DelayReason = null,
    string ReportProgressStatus = "NOT_STARTED",
    // STAT / URGENT / ROUTINE — drives the worklist sort + chip colour.
    string Priority = "ROUTINE",
    // Turnaround-time milestones (UTC, all nullable).
    DateTime? ArrivedAt = null,
    DateTime? ScanStartedAt = null,
    DateTime? DeliveredAt = null,
    // Denormalised latest-comment byline so worklist rows can show
    // "by {name} · {when}" without joining Users.
    string? LatestCommentAuthorName = null,
    DateTime? LatestCommentAt = null,
    // Sync engine fields. UpdatedAt drives "what's the high-water mark
    // I should send as ?updatedAfter= on the next pull?". DeletedAt
    // makes a tombstone visible to the client so it can purge its
    // local cache.
    DateTime? UpdatedAt = null,
    DateTime? DeletedAt = null
);

