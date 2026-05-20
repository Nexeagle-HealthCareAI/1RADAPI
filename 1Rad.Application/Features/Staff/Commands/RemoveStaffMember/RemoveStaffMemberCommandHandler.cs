using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.RemoveStaffMember;

public class RemoveStaffMemberCommandHandler : IRequestHandler<RemoveStaffMemberCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public RemoveStaffMemberCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(RemoveStaffMemberCommand request, CancellationToken cancellationToken)
    {
        var member = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);

        if (member == null)
            return (false, "Staff member not found.");

        // Soft-delete: mark inactive rather than hard delete to preserve payroll history
        member.Status    = "Inactive";
        member.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
