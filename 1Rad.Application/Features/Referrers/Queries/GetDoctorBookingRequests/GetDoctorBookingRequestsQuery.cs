using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetDoctorBookingRequests;

public record DoctorBookingRequestDto(
    Guid Id,
    string PatientName,
    string? Modality,
    string? ServiceName,
    string? PreferredDate,   // yyyy-MM-dd
    string Status,           // PENDING | SCHEDULED | DECLINED
    string? DeclineReason,
    string CreatedAt         // ISO UTC
);

/// <summary>"My requests" on the doctor portal - every booking this doctor has asked for, newest
/// first, so they can see what's pending, booked, or declined without calling the centre. Anonymous
/// (capability-link) caller.</summary>
public record GetDoctorBookingRequestsQuery(Guid ReferrerId) : IRequest<List<DoctorBookingRequestDto>>;

public class GetDoctorBookingRequestsQueryHandler : IRequestHandler<GetDoctorBookingRequestsQuery, List<DoctorBookingRequestDto>>
{
    private readonly IApplicationDbContext _context;

    public GetDoctorBookingRequestsQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<DoctorBookingRequestDto>> Handle(GetDoctorBookingRequestsQuery request, CancellationToken ct)
    {
        return await _context.ReferralBookingRequests.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.ReferrerId == request.ReferrerId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .Select(r => new DoctorBookingRequestDto(
                r.Id,
                r.PatientName,
                r.Modality,
                r.ServiceName,
                r.PreferredDate != null ? r.PreferredDate.Value.ToString("yyyy-MM-dd") : null,
                r.Status,
                r.DeclineReason,
                r.CreatedAt.ToString("o")))
            .ToListAsync(ct);
    }
}
