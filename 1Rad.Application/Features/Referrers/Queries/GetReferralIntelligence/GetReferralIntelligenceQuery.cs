using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

public record GetReferralIntelligenceQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null
) : IRequest<List<ReferrerIntelligenceDto>>;

public class GetReferralIntelligenceQueryHandler : IRequestHandler<GetReferralIntelligenceQuery, List<ReferrerIntelligenceDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferralIntelligenceQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReferrerIntelligenceDto>> Handle(GetReferralIntelligenceQuery request, CancellationToken cancellationToken)
    {
        var patientsQuery = _context.Patients
            .AsNoTracking()
            .Include(p => p.Referrer)
            .AsQueryable();

        // Filter by Specific Referrer if requested
        if (request.ReferrerId.HasValue)
        {
            patientsQuery = patientsQuery.Where(p => p.ReferrerId == request.ReferrerId.Value);
        }
        else
        {
            // Only include patients that HAVE a referrer for this specific intelligence query
            patientsQuery = patientsQuery.Where(p => p.ReferrerId != null);
        }

        // Materialize and Group
        // We include Appointments to get modality details for each patient
        var patientsWithAppointments = await patientsQuery
            .Select(p => new
            {
                p.ReferrerId,
                ReferrerName = p.Referrer != null ? p.Referrer.Name : "Direct",
                ReferrerContact = p.Referrer != null ? p.Referrer.Contact : "N/A",
                ReferrerAddress = p.Referrer != null ? p.Referrer.Address : "N/A",
                Patient = p,
                LatestAppointment = _context.Appointments
                    .Where(a => a.PatientId == p.PatientId)
                    .OrderByDescending(a => a.DateTime)
                    .Select(a => new { a.AppointmentId, a.Modality, a.Service, a.Status, a.DateTime })
                    .FirstOrDefault(),
                Commission = _context.ReferralCommissions
                    .Where(c => _context.Appointments.Where(a => a.PatientId == p.PatientId).Select(a => (Guid?)a.AppointmentId).Contains(c.AppointmentId))
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new { c.CommissionAmount, c.Status })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        // Apply temporal filters on appointments if provided
        if (request.StartDate.HasValue || request.EndDate.HasValue)
        {
            patientsWithAppointments = patientsWithAppointments
                .Where(x => x.LatestAppointment != null &&
                           (!request.StartDate.HasValue || x.LatestAppointment.DateTime >= request.StartDate.Value) &&
                           (!request.EndDate.HasValue || x.LatestAppointment.DateTime <= request.EndDate.Value.Date.AddDays(1).AddTicks(-1)))
                .ToList();
        }

        // Group by Referrer
        var result = patientsWithAppointments
            .GroupBy(x => x.ReferrerId)
            .Select(g => {
                var patientsList = g.Select(x => new ReferredPatientDto(
                    x.Patient.PatientId,
                    x.Patient.PatientIdentifier,
                    x.Patient.FullName,
                    x.Patient.Mobile,
                    x.Patient.Address,
                    x.Patient.Age,
                    x.Patient.Gender,
                    x.LatestAppointment?.Modality ?? "RECON",
                    x.LatestAppointment?.Service ?? "N/A",
                    x.Patient.SourceOfInfo ?? "DIRECT",
                    x.LatestAppointment?.DateTime.ToString("yyyy-MM-dd") ?? "N/A",
                    x.LatestAppointment?.Status ?? "COMMITTED",
                    x.LatestAppointment?.AppointmentId,
                    x.Commission?.CommissionAmount ?? 0,
                    x.Commission?.Status ?? "Unpaid"
                )).ToList();

                var totalComm = patientsList.Sum(p => p.CommissionAmount);
                var paidComm = patientsList.Where(p => p.CommissionStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase)).Sum(p => p.CommissionAmount);

                return new ReferrerIntelligenceDto(
                    g.Key ?? Guid.Empty,
                    g.First().ReferrerName,
                    g.First().ReferrerContact,
                    g.First().ReferrerAddress,
                    g.Count(),
                    patientsList,
                    totalComm,
                    paidComm,
                    totalComm - paidComm
                );
            })
            .OrderByDescending(r => r.TotalPatients)
            .ToList();

        return result;
    }
}
