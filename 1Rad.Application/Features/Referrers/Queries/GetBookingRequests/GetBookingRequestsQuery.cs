using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetBookingRequests;

public record BookingRequestDto(
    Guid Id,
    Guid ReferrerId,
    string ReferrerName,
    string? ReferrerContact,
    string PatientName,
    string? Mobile,
    string? Age,
    string? Gender,
    string? Modality,
    string? ServiceName,
    string? PreferredDate,   // yyyy-MM-dd
    string? Notes,
    string Status,
    string? DeclineReason,
    Guid? ResultingAppointmentId,
    string CreatedAt         // ISO UTC
);

/// <summary>The front desk's queue of patients referring doctors have asked to be booked, from
/// their portal links. Staff-authenticated - scoped to the caller's hospital by the normal query
/// filter. Pending requests first (oldest first, so nobody waits behind a newer one); the most
/// recently decided ones trail behind for a short audit trail.</summary>
public record GetBookingRequestsQuery(bool IncludeDecided = true) : IRequest<List<BookingRequestDto>>;

public class GetBookingRequestsQueryHandler : IRequestHandler<GetBookingRequestsQuery, List<BookingRequestDto>>
{
    private readonly IApplicationDbContext _context;

    public GetBookingRequestsQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<BookingRequestDto>> Handle(GetBookingRequestsQuery request, CancellationToken ct)
    {
        var query = _context.ReferralBookingRequests.AsNoTracking().AsQueryable();
        if (!request.IncludeDecided) query = query.Where(r => r.Status == "PENDING");
        // Keep the decided tail bounded so a busy centre's history doesn't grow the queue forever.
        var cutoff = DateTime.UtcNow.AddDays(-14);
        query = query.Where(r => r.Status == "PENDING" || r.CreatedAt >= cutoff);

        // Sorted in memory (this set is small - pending is never trimmed, decided is capped to the
        // last 14 days) so the two different orderings below (oldest-pending-first, newest-decided-
        // first) don't depend on how a provider translates a conditional ORDER BY.
        var rows = (await query
            .Select(r => new
            {
                r.Id, r.ReferrerId, r.PatientName, r.Mobile, r.Age, r.Gender, r.Modality, r.ServiceName,
                r.PreferredDate, r.Notes, r.Status, r.DeclineReason, r.ResultingAppointmentId, r.CreatedAt,
            })
            .ToListAsync(ct))
            .OrderBy(r => r.Status == "PENDING" ? 0 : 1)
            .ThenBy(r => r.Status == "PENDING" ? r.CreatedAt : default)
            .ThenByDescending(r => r.Status != "PENDING" ? r.CreatedAt : default)
            .ToList();

        var referrerIds = rows.Select(r => r.ReferrerId).Distinct().ToList();
        var referrers = await _context.Referrers.AsNoTracking()
            .Where(r => referrerIds.Contains(r.ReferrerId))
            .Select(r => new { r.ReferrerId, r.Name, r.Contact })
            .ToDictionaryAsync(r => r.ReferrerId, ct);

        return rows.Select(r =>
        {
            referrers.TryGetValue(r.ReferrerId, out var match);
            return new BookingRequestDto(
                r.Id, r.ReferrerId, match?.Name ?? "Unknown", match?.Contact,
                r.PatientName, r.Mobile, r.Age, r.Gender, r.Modality, r.ServiceName,
                r.PreferredDate != null ? r.PreferredDate.Value.ToString("yyyy-MM-dd") : null,
                r.Notes, r.Status, r.DeclineReason, r.ResultingAppointmentId, r.CreatedAt.ToString("o"));
        }).ToList();
    }
}
