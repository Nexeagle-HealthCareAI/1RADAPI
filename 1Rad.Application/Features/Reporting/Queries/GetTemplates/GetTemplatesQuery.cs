using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.GetTemplates;

public record GetTemplatesQuery : IRequest<List<ReportTemplate>>
{
    public string? Modality { get; init; }
}

public class GetTemplatesQueryHandler : IRequestHandler<GetTemplatesQuery, List<ReportTemplate>>
{
    private readonly IApplicationDbContext _context;

    public GetTemplatesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReportTemplate>> Handle(GetTemplatesQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        var query = _context.ReportTemplates
            .Where(t => t.HospitalId == hospitalId && (t.DoctorId == null || t.DoctorId == doctorId));

        if (!string.IsNullOrEmpty(request.Modality) && request.Modality != "ALL")
        {
            query = query.Where(t => t.Modality == request.Modality);
        }

        return await query.ToListAsync(cancellationToken);
    }
}
