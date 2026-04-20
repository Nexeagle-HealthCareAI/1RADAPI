using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Patients.Queries.GetPatients;

public record GetPatientsQuery(string? SearchQuery = null, DateTime? StartDate = null, DateTime? EndDate = null) : IRequest<List<PatientDto>>;

public class GetPatientsQueryHandler : IRequestHandler<GetPatientsQuery, List<PatientDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPatientsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PatientDto>> Handle(GetPatientsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Patients.AsNoTracking();

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(p => 
                p.FullName.ToLower().Contains(search) || 
                p.Mobile.Contains(search) || 
                p.PatientIdentifier.ToLower().Contains(search));
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
                p.FullName,
                p.Mobile,
                p.Age,
                p.Gender,
                p.Village,
                p.District,
                p.Address,
                p.PatientIdentifier,
                p.SourceOfInfo,
                p.CreatedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
