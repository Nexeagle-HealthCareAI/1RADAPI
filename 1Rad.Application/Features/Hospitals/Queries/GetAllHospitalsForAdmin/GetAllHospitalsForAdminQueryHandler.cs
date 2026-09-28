using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Hospitals.Queries.GetAllHospitalsForAdmin;

// Every registered diagnostic center + its active staff roster, for CMS's admin console.
// Deliberately does NOT filter by IUserContext.AuthorizedHospitalIds (unlike
// GetGroupHospitalsQuery) -- this is cross-tenant by design, reachable only through the
// service-key gated HospitalsController.GetAllForAdmin endpoint, never a user JWT.
public class GetAllHospitalsForAdminQueryHandler : IRequestHandler<GetAllHospitalsForAdminQuery, List<AdminHospitalDto>>
{
    private readonly IApplicationDbContext _context;

    public GetAllHospitalsForAdminQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<AdminHospitalDto>> Handle(GetAllHospitalsForAdminQuery request, CancellationToken cancellationToken)
    {
        var hospitals = await _context.Hospitals.AsNoTracking().ToListAsync(cancellationToken);

        var subscriptions = await _context.HospitalSubscriptions
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var latestSubByHospital = subscriptions
            .GroupBy(s => s.HospitalId)
            .ToDictionary(g => g.Key, g => g.First());

        var staff = await _context.StaffMembers
            .AsNoTracking()
            .Where(s => s.Status == "Active")
            .ToListAsync(cancellationToken);
        var staffByHospital = staff.GroupBy(s => s.HospitalId).ToDictionary(g => g.Key, g => g.ToList());

        return hospitals
            .Select(h =>
            {
                latestSubByHospital.TryGetValue(h.HospitalId, out var sub);
                staffByHospital.TryGetValue(h.HospitalId, out var staffList);

                return new AdminHospitalDto(
                    h.HospitalId,
                    h.HospitalName ?? "Unknown",
                    h.HospitalAddress ?? "Unknown",
                    h.GSTIN,
                    h.RegistrationNumber,
                    h.NABHNumber,
                    h.Status,
                    h.CreatedAt,
                    sub?.Status ?? "None",
                    sub?.BillingCycle ?? "None",
                    sub?.Modules ?? "None",
                    (staffList ?? new List<StaffMember>())
                        .Select(s => new AdminStaffDto(s.StaffId, s.FullName, s.Email, s.Mobile, s.Designation, s.Specialization, s.Status))
                        .ToList()
                );
            })
            .OrderByDescending(h => h.CreatedAt)
            .ToList();
    }
}
