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
        // 1. Initialize Appointment-Centric Query (Every appointment is a 'Mission')
        var appointmentsQuery = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Patient)
            .Include(a => a.Patient.Referrer)
            .Where(a => a.Patient.ReferrerId != null) // Focus on referred cases
            .AsQueryable();

        // 2. Apply Institutional Routing Filters
        if (request.ReferrerId.HasValue)
        {
            appointmentsQuery = appointmentsQuery.Where(a => a.Patient.ReferrerId == request.ReferrerId.Value);
        }

        // 3. Precise Temporal Filtering (Applied before materialization for integrity)
        if (request.StartDate.HasValue)
        {
            appointmentsQuery = appointmentsQuery.Where(a => a.DateTime >= request.StartDate.Value);
        }
        if (request.EndDate.HasValue)
        {
            var endOfDay = request.EndDate.Value.Date.AddDays(1).AddTicks(-1);
            appointmentsQuery = appointmentsQuery.Where(a => a.DateTime <= endOfDay);
        }

        // 4. Materialize Tactical Mission Data with Commission Context
        var missionData = await appointmentsQuery
            .Select(a => new
            {
                ReferrerId = a.Patient.ReferrerId ?? Guid.Empty,
                ReferrerName = a.Patient.Referrer.Name ?? "Anonymous Source",
                ReferrerContact = a.Patient.Referrer.Contact ?? "N/A",
                ReferrerAddress = a.Patient.Referrer.Address ?? "N/A",
                Appointment = a,
                Patient = a.Patient,
                Commission = _context.ReferralCommissions
                    .Where(c => c.AppointmentId == a.AppointmentId)
                    .Select(c => new { c.CommissionAmount, c.Status })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        // 5. Aggregate Intelligence by Referrer Node
        var result = missionData
            .GroupBy(m => m.ReferrerId)
            .Select(g => {
                var missionsList = g.Select(m => new ReferredPatientDto(
                    m.Patient.PatientId,
                    m.Patient.PatientIdentifier,
                    m.Patient.FullName,
                    m.Patient.Mobile,
                    m.Patient.Address,
                    m.Patient.Age,
                    m.Patient.Gender,
                    m.Appointment.Modality,
                    m.Appointment.Service,
                    m.Patient.SourceOfInfo ?? "DIRECT",
                    m.Appointment.DateTime.ToString("yyyy-MM-dd"),
                    m.Appointment.Status,
                    m.Appointment.AppointmentId,
                    m.Commission?.CommissionAmount ?? 0,
                    m.Commission?.Status ?? "Unpaid"
                )).ToList();

                var totalComm = missionsList.Sum(p => p.CommissionAmount);
                var paidComm = missionsList.Where(p => p.CommissionStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase)).Sum(p => p.CommissionAmount);

                return new ReferrerIntelligenceDto(
                    g.Key,
                    g.First().ReferrerName,
                    g.First().ReferrerContact,
                    g.First().ReferrerAddress,
                    missionsList.Count, // Every referred appointment is a mission unit
                    missionsList,
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
