using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.SwitchContext;

public class SwitchContextCommandHandler : IRequestHandler<SwitchContextCommand, SwitchContextResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;
    private readonly IJwtProvider _jwtProvider;
    private readonly ILogger<SwitchContextCommandHandler> _logger;

    public SwitchContextCommandHandler(
        IApplicationDbContext context,
        IUserContext userContext,
        IJwtProvider jwtProvider,
        ILogger<SwitchContextCommandHandler> logger)
    {
        _context = context;
        _userContext = userContext;
        _jwtProvider = jwtProvider;
        _logger = logger;
    }

    public async Task<SwitchContextResponse> Handle(SwitchContextCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var userId = _userContext.UserId;
            _logger.LogInformation("Attempting context switch for user {UserId} to hospital {TargetHospitalId}", userId, request.TargetHospitalId);

            // 1. Verify mapping and retrieve specific context
            var mapping = await _context.UserHospitalMappings
                .Include(m => m.User)
                .Include(m => m.Hospital)
                .Include(m => m.Roles)
                .Include(m => m.CustomRoles)
                .FirstOrDefaultAsync(m => m.UserId == userId && m.HospitalId == request.TargetHospitalId, cancellationToken);

            if (mapping == null)
            {
                _logger.LogWarning("Context switch failed: User {UserId} is not authorized for hospital {TargetHospitalId}", userId, request.TargetHospitalId);
                return new SwitchContextResponse { Success = false, Error = "You do not have access to the selected hospital." };
            }

            // 2. Fetch all authorized hubs for the token payload
            var authorizedHospitalIds = await _context.UserHospitalMappings
                .Where(m => m.UserId == userId)
                .Select(m => m.HospitalId)
                .ToListAsync(cancellationToken);

            // 3. Generate New Contextual JWT — carrying the SAME session id
            //    so the user's continuous presence on this device stays one
            //    session through a hospital switch (and the Active Sessions
            //    UI doesn't flicker).
            var accessToken = _jwtProvider.GenerateContextualToken(
                mapping.User, mapping, authorizedHospitalIds, _userContext.SessionId);

            return new SwitchContextResponse
            {
                Success = true,
                AccessToken = accessToken,
                Roles = mapping.Roles.Select(r => r.RoleName).Concat(mapping.CustomRoles.Select(cr => cr.RoleName)).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during context switch for hospital {TargetHospitalId}", request.TargetHospitalId);
            return new SwitchContextResponse { Success = false, Error = "An internal server error occurred." };
        }
    }
}
