using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.GetKeywords;

public record GetKeywordsQuery : IRequest<List<ReportingKeyword>>;

public class GetKeywordsQueryHandler : IRequestHandler<GetKeywordsQuery, List<ReportingKeyword>>
{
    private readonly IApplicationDbContext _context;

    public GetKeywordsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReportingKeyword>> Handle(GetKeywordsQuery request, CancellationToken cancellationToken)
    {
        var doctorId = _context.UserContext.UserId;
        
        return await _context.ReportingKeywords
            .Where(k => k.DoctorId == doctorId)
            .ToListAsync(cancellationToken);
    }
}
