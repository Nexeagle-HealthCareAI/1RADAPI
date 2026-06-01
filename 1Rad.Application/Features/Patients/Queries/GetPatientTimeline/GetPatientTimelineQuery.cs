using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Patients.Queries.GetPatientTimeline;

public record GetPatientTimelineQuery(Guid PatientId) : IRequest<List<PatientTimelineDto>>;

public class GetPatientTimelineQueryHandler : IRequestHandler<GetPatientTimelineQuery, List<PatientTimelineDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPatientTimelineQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PatientTimelineDto>> Handle(GetPatientTimelineQuery request, CancellationToken cancellationToken)
    {
        var appointments = await _context.Appointments
            .Where(a => a.PatientId == request.PatientId)
            .OrderByDescending(a => a.DateTime)
            .Select(a => new
            {
                Appointment = a,
                Report = _context.DiagnosticReports
                    .Where(dr => dr.AppointmentId == a.AppointmentId)
                    .Select(dr => new TimelineReportDto(
                        dr.Id,
                        dr.Findings,
                        dr.Impression,
                        dr.Advice,
                        dr.CreatedAt,
                        dr.Doctor != null ? dr.Doctor.FullName : "Unknown Doctor"
                    ))
                    .FirstOrDefault(),
                Assets = _context.StudyAssets
                    .Where(sa => sa.AppointmentId == a.AppointmentId)
                    .Select(sa => new TimelineAssetDto(
                        sa.Id,
                        sa.FileName,
                        sa.FileType,
                        sa.BlobUrl,
                        sa.UploadedAt
                    ))
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        // Batched second query to pull every live service line for the
        // visits we just fetched. Single round-trip regardless of how
        // many visits this patient has — same pattern as
        // GetAppointmentsQuery's services materialisation.
        var appointmentIds = appointments.Select(x => x.Appointment.AppointmentId).ToList();
        var serviceRows = await _context.AppointmentServices
            .AsNoTracking()
            .Where(s => appointmentIds.Contains(s.AppointmentId) && s.DeletedAt == null)
            .OrderBy(s => s.UpdatedAt)
            .Select(s => new
            {
                s.AppointmentId,
                Dto = new TimelineServiceDto(
                    s.Id,
                    s.ServiceName ?? string.Empty,
                    s.Modality ?? string.Empty,
                    s.Status ?? "NOT_STARTED"
                )
            })
            .ToListAsync(cancellationToken);

        var servicesByAppointment = serviceRows
            .GroupBy(s => s.AppointmentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TimelineServiceDto>)g.Select(x => x.Dto).ToList());

        return appointments.Select(x => new PatientTimelineDto(
            x.Appointment.AppointmentId,
            x.Appointment.DisplayId ?? string.Empty,
            x.Appointment.Service ?? string.Empty,
            x.Appointment.Modality ?? string.Empty,
            x.Appointment.DateTime,
            x.Appointment.Status ?? "BOOKED",
            x.Appointment.ReferredBy ?? string.Empty,
            x.Appointment.ReferredContact ?? string.Empty,
            x.Report,
            x.Assets,
            servicesByAppointment.TryGetValue(x.Appointment.AppointmentId, out var lines)
                ? lines
                : (IReadOnlyList<TimelineServiceDto>)System.Array.Empty<TimelineServiceDto>()
        )).ToList();
    }
}
