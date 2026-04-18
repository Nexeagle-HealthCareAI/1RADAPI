using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Commands.RemoveStaff;

public class RemoveStaffCommandHandler : IRequestHandler<RemoveStaffCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public RemoveStaffCommandHandler(IApplicationDbContext _context)
    {
        this._context = _context;
    }

    public async Task<(bool Success, string? Error)> Handle(RemoveStaffCommand request, CancellationToken cancellationToken)
    {
        var mapping = await _context.UserHospitalMappings
            .FirstOrDefaultAsync(m => m.UserId == request.UserId && m.HospitalId == request.HospitalId, cancellationToken);

        if (mapping == null) return (false, "Staff mapping for this hospital not found.");

        _context.UserHospitalMappings.Remove(mapping);
        await _context.SaveChangesAsync(cancellationToken);

        return (true, null);
    }
}
