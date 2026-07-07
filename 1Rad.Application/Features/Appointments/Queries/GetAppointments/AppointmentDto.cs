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
    DateTime? DeletedAt = null,
    // Multi-service visit line items (step 2). Null on responses from
    // servers that haven't been upgraded yet; empty list once the
    // server is on the multi-service code path but the visit happens
    // to only carry one service. Frontends that haven't migrated yet
    // can still read the scalar Service / Modality / Amount fields
    // above — they reflect the primary (first) service line.
    IReadOnlyList<AppointmentServiceDto>? Services = null,
    // The DOCTOR who referred this patient, resolved from the referral source:
    // if the referrer is a doctor it's their name; if the referrer is an agent
    // it's the doctor they're "supported by". Populated on the single-record
    // (reporting) fetch so the report always shows a doctor as "Referred By".
    string? ReferringDoctorName = null,
    // The per-appointment supporting doctor (set when ReferredBy is an agent).
    // Lets the edit drawer prefill THIS visit's doctor without re-typing, and
    // syncs offline like the other scalar fields.
    string? SupportedByDoctor = null,
    string? Village = null,
    string? Block = null,
    string? District = null,
    string? Address = null,
    string? SourceOfInfo = null,
    string? ReferrerDegree = null,
    string? ReferrerSpecialty = null
);

/// <summary>
/// One service line on an appointment. Mirrors the AppointmentService
/// entity but exposes only what the worklist / booking UI need to
/// render and edit. The Id matches the DB row so the frontend can
/// send it back in the Services array on PUT to keep that row in
/// place rather than recreating it.
/// </summary>
public record AppointmentServiceDto(
    Guid Id,
    string ServiceName,
    string Modality,
    decimal Amount,
    decimal ReferralCutValue,
    string Status,
    DateTime? ScanStartedAt,
    DateTime? ScanCompletedAt,
    DateTime? ReportedAt,
    DateTime? DeliveredAt,
    DateTime? CancelledAt,
    Guid? TechnicianId,
    Guid? ServiceChargeId,
    DateTime UpdatedAt,
    string? TechnicianComments = null
);
