using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetPatientSourceBreakdown;

/// <param name="Source">A channel from <see cref="PatientSources"/>: one of the dropdown values, "Other", or "Not recorded".</param>
/// <param name="Visits">Attended visits by patients who came through this channel.</param>
/// <param name="Patients">Distinct patients behind those visits.</param>
/// <param name="NewPatients">Patients whose first-ever attended visit at the centre is one of these visits.</param>
/// <param name="PartnerVisits">Of the visits, how many are credited to a real referral partner (as opposed to Self, an unlinked name or nobody).</param>
public record PatientSourceRowDto(string Source, int Visits, int Patients, int NewPatients, int PartnerVisits);

public record PatientSourceBreakdownDto(
    List<PatientSourceRowDto> Rows,
    int TotalVisits,
    int TotalPatients,
    int TotalNewPatients);

/// <summary>
/// "How did patients hear about us", totalled. Counts visits where the patient actually arrived
/// (the same rule as Source Analytics), in IST days, by the channel recorded on the patient.
/// Together with the partner columns it shows where the two views disagree - e.g. patients who
/// say "By Doctor" but whose visits carry no partner (referral credit that is never paid).
/// </summary>
public record GetPatientSourceBreakdownQuery(DateTime? StartDate = null, DateTime? EndDate = null)
    : IRequest<PatientSourceBreakdownDto>;

public class GetPatientSourceBreakdownQueryHandler : IRequestHandler<GetPatientSourceBreakdownQuery, PatientSourceBreakdownDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetPatientSourceBreakdownQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<PatientSourceBreakdownDto> Handle(GetPatientSourceBreakdownQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        var query = _context.Appointments.AsNoTracking()
            .Where(a => a.HospitalId == hospitalId && a.Status != "CANCELLED");
        if (request.StartDate.HasValue)
        {
            var fromUtc = IstDateRange.ToUtcStart(request.StartDate.Value);
            query = query.Where(a => a.DateTime >= fromUtc);
        }
        if (request.EndDate.HasValue)
        {
            var toUtc = IstDateRange.ToUtcEndInclusive(request.EndDate.Value);
            query = query.Where(a => a.DateTime <= toUtc);
        }

        var attended = (await query
                .Select(a => new
                {
                    a.AppointmentId,
                    a.PatientId,
                    a.Status,
                    a.ArrivedAt,
                    a.ReferredBy,
                    AppointmentReferrerId = a.ReferrerId,
                    PatientReferrerId = a.Patient.ReferrerId,
                    Channel = a.Patient.SourceOfInfo,
                })
                .ToListAsync(cancellationToken))
            .Where(v => AppointmentAttendance.IsAttended(v.Status, v.ArrivedAt))
            .ToList();

        var attribution = new ReferralAttribution(
            (await _context.Referrers.AsNoTracking()
                .Where(r => r.HospitalId == hospitalId)
                .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt })
                .ToListAsync(cancellationToken))
            .Select(r => new ReferralAttribution.Entry(r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt)));

        // A patient's FIRST attended visit at the centre (any date), so someone seen for years is
        // not "new" just because this range happens to hold their latest visit.
        var patientIds = attended.Select(v => v.PatientId).Distinct().ToList();
        var firstVisitByPatient = patientIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : (await _context.Appointments.AsNoTracking()
                    .Where(a => a.HospitalId == hospitalId && patientIds.Contains(a.PatientId)
                                && a.Status != "CANCELLED"
                                && (a.ArrivedAt != null || AppointmentAttendance.AttendedStatuses.Contains(a.Status!)))
                    .Select(a => new { a.PatientId, a.AppointmentId, a.DateTime })
                    .ToListAsync(cancellationToken))
                .GroupBy(a => a.PatientId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DateTime).ThenBy(x => x.AppointmentId).First().AppointmentId);

        var rows = attended
            .Select(v => new
            {
                Channel = PatientSources.Classify(v.Channel),
                v.PatientId,
                v.AppointmentId,
                IsFirstVisit = firstVisitByPatient.TryGetValue(v.PatientId, out var first) && first == v.AppointmentId,
                IsPartner = attribution.Attribute(v.ReferredBy, v.PatientReferrerId, v.AppointmentReferrerId).Kind == SourceKind.Partner,
            })
            .GroupBy(x => x.Channel)
            .Select(g => new PatientSourceRowDto(
                g.Key,
                g.Count(),
                g.Select(x => x.PatientId).Distinct().Count(),
                g.Count(x => x.IsFirstVisit),
                g.Count(x => x.IsPartner)))
            // Biggest channel first; "Not recorded" always last so it reads as a data-quality line.
            .OrderBy(r => r.Source == PatientSources.NotRecorded ? 1 : 0)
            .ThenByDescending(r => r.Visits)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ToList();

        return new PatientSourceBreakdownDto(
            rows,
            rows.Sum(r => r.Visits),
            patientIds.Count,
            rows.Sum(r => r.NewPatients));
    }
}
