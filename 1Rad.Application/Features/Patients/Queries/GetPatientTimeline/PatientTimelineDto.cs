using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Patients.Queries.GetPatientTimeline;

public record PatientTimelineDto(
    Guid AppointmentId,
    string DisplayId,
    string Service,
    string Modality,
    DateTime DateTime,
    string Status,
    string ReferredBy,
    string ReferredContact,
    TimelineReportDto? Report,
    List<TimelineAssetDto> Assets,
    // Multi-service rollout (batch-1 fix). Service lines on this visit
    // so the frontend chip stack + per-line render works on the patient
    // timeline. Null = response from a server build that pre-dates this
    // field — the page's `getServiceLines` helper still falls back to
    // the scalar Service/Modality fields above.
    IReadOnlyList<TimelineServiceDto>? Services = null
);

/// <summary>
/// One line item on a visit, surfaced through the patient timeline so
/// the chip stack + per-service status dot render correctly. Mirrors
/// the worklist's AppointmentServiceDto but trimmed to the fields the
/// timeline actually uses.
/// </summary>
public record TimelineServiceDto(
    Guid Id,
    string ServiceName,
    string Modality,
    string Status
);

public record TimelineReportDto(
    Guid ReportId,
    string Findings,
    string Impression,
    string Advice,
    DateTime? CreatedAt,
    string DoctorName
);

public record TimelineAssetDto(
    Guid AssetId,
    string FileName,
    string FileType,
    string BlobUrl,
    DateTime? UploadedAt
);

