using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.DeleteSalaryRevision;

public class DeleteSalaryRevisionCommandHandler : IRequestHandler<DeleteSalaryRevisionCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public DeleteSalaryRevisionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(DeleteSalaryRevisionCommand request, CancellationToken cancellationToken)
    {
        var rev = await _context.SalaryRevisions
            .FirstOrDefaultAsync(r => r.RevisionId == request.RevisionId
                                   && r.StaffId == request.StaffId
                                   && r.HospitalId == request.HospitalId,
                                 cancellationToken);
        if (rev == null) return (false, "Revision not found.");

        _context.SalaryRevisions.Remove(rev);
        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
