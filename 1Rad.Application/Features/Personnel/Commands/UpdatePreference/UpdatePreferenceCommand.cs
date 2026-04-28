using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Commands.UpdatePreference;

public record UpdatePreferenceCommand : IRequest<bool>
{
    public string PreferenceKey { get; init; } = string.Empty; // e.g., "ReportingMode"
    public string PreferenceValue { get; init; } = string.Empty; // e.g., "Narrative Editor"
}

public class UpdatePreferenceCommandHandler : IRequestHandler<UpdatePreferenceCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdatePreferenceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdatePreferenceCommand request, CancellationToken cancellationToken)
    {
        var userId = _context.UserContext.UserId;

        var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);
        if (user == null) return false;

        if (request.PreferenceKey == "ReportingMode")
        {
            user.PreferredReportingMode = request.PreferenceValue;
        }
        else
        {
            return false;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
