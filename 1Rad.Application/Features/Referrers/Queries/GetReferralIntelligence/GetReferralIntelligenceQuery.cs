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
    private readonly IUserContext _userContext;

    public GetReferralIntelligenceQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<ReferrerIntelligenceDto>> Handle(GetReferralIntelligenceQuery request, CancellationToken cancellationToken)
    {
        // 1. Initialize Appointment-Centric Query (Every appointment is a 'Mission')
        var hospitalId = _userContext.HospitalId;
        var appointmentsQuery = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Patient)
            .Include(a => a.Patient.Referrer)
            .Where(a => a.HospitalId == hospitalId)
            .Where(a => a.Patient.ReferrerId != null || !string.IsNullOrEmpty(a.ReferredBy)) // Broaden detection
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

        // 4. Materialize Tactical Mission Data with Commission & Revenue Context
        var missionData = await appointmentsQuery
            .Select(a => new
            {
                ReferrerId = (a.Patient.ReferrerId ?? 
                             _context.Referrers.Where(r => r.Name == a.ReferredBy && r.HospitalId == a.HospitalId).Select(r => r.ReferrerId).FirstOrDefault()) ?? Guid.Empty,
                ReferrerName = a.Patient.Referrer.Name ?? a.ReferredBy ?? "Anonymous Source",
                ReferrerContact = a.Patient.Referrer.Contact ?? "N/A",
                ReferrerAddress = a.Patient.Referrer.Address ?? "N/A",
                Appointment = a,
                Patient = a.Patient,
                Commissions = _context.ReferralCommissions
                    .Where(c => c.AppointmentId == a.AppointmentId && c.Status != "Cancelled")
                    .Select(c => new { c.CommissionAmount, c.Status })
                    .ToList(),
                Revenue = _context.Invoices
                    .Where(i => i.AppointmentId == a.AppointmentId)
                    .Select(i => i.TotalAmount)
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
                    m.Commissions.Sum(c => c.CommissionAmount),
                    m.Commissions.Any(c => c.Status.Equals("UNPAID", StringComparison.OrdinalIgnoreCase)) ? "Unpaid" : "Paid",
                    m.Revenue,
                    m.ReferrerName
                )).ToList();

                var totalComm = missionsList.Sum(p => p.CommissionAmount);
                var paidComm = missionsList.Where(p => p.CommissionStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase)).Sum(p => p.CommissionAmount);
                var totalRev = missionsList.Sum(p => p.TotalAmount);

                return new ReferrerIntelligenceDto(
                    g.Key,
                    g.First().ReferrerName,
                    g.First().ReferrerContact,
                    g.First().ReferrerAddress,
                    missionsList.Count, // Every referred appointment is a mission unit
                    missionsList,
                    totalComm,
                    paidComm,
                    totalComm - paidComm,
                    totalRev
                );
            })
            .OrderByDescending(r => r.TotalPatients)
            .ToList();

        return result;
    }
}
