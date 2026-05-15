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
    TimelineReportDto? Report,
    List<TimelineAssetDto> Assets
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

