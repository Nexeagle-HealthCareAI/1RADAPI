using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Patients.Queries.GetPatients;

// UpdatedAfter + IncludeDeleted are the Phase B1 sync-engine knobs (same
// shape as GetAppointmentsQuery). UpdatedAfter restricts the result to
// rows that changed since the client's last pull; IncludeDeleted opts the
// sync engine into seeing tombstones so it can purge its local cache.
public record GetPatientsQuery(
    string? SearchQuery = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    DateTime? UpdatedAfter = null,
    bool IncludeDeleted = false
) : IRequest<List<PatientDto>>;

public class GetPatientsQueryHandler : IRequestHandler<GetPatientsQuery, List<PatientDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPatientsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PatientDto>> Handle(GetPatientsQuery request, CancellationToken cancellationToken)
    {
        // Hospital tenancy guard. Pre-B1 the query relied on the controller
        // being called inside a hospital scope; the sync engine pulls
        // unbounded deltas so we now enforce it here too.
        if (_context.UserContext.HospitalId == Guid.Empty)
        {
            return new List<PatientDto>();
        }

        var query = _context.Patients
            .Where(p => p.HospitalId == _context.UserContext.HospitalId)
            .AsNoTracking();

        // Tombstone filter — hide soft-deleted rows from the regular UI;
        // sync engine flips IncludeDeleted on so it can apply DELETE
        // semantics to the local cache.
        if (!request.IncludeDeleted)
        {
            query = query.Where(p => p.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            query = query.Where(p => p.UpdatedAt > since);
        }

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(p =>
                (p.FullName != null && p.FullName.ToLower().Contains(search)) ||
                (p.Mobile != null && p.Mobile.Contains(search)) ||
                (p.PatientIdentifier != null && p.PatientIdentifier.ToLower().Contains(search)));
        }

        if (request.StartDate.HasValue)
        {
            query = query.Where(p => p.CreatedAt.Date >= request.StartDate.Value.Date);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(p => p.CreatedAt.Date <= request.EndDate.Value.Date);
        }

        return await query
            .Select(p => new PatientDto(
                p.PatientId,
                p.FullName ?? string.Empty,
                p.Mobile ?? string.Empty,
                p.Age ?? string.Empty,
                p.Gender ?? string.Empty,
                p.Village ?? string.Empty,
                p.Block ?? string.Empty,
                p.District ?? string.Empty,
                p.Address ?? string.Empty,
                p.PatientIdentifier ?? string.Empty,
                p.SourceOfInfo ?? string.Empty,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeletedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
