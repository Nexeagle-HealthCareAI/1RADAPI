using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Users.Commands.UpdateClinicalCredentials;

public class UpdateClinicalCredentialsCommandHandler
    : IRequestHandler<UpdateClinicalCredentialsCommand, UpdateClinicalCredentialsResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<UpdateClinicalCredentialsCommandHandler> _logger;

    public UpdateClinicalCredentialsCommandHandler(
        IApplicationDbContext context,
        ILogger<UpdateClinicalCredentialsCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<UpdateClinicalCredentialsResponse> Handle(
        UpdateClinicalCredentialsCommand request,
        CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.UserId == request.UserId, cancellationToken);

        if (user == null)
        {
            return new UpdateClinicalCredentialsResponse(
                Success: false,
                Error: "User not found.",
                ErrorCode: "USER_NOT_FOUND");
        }

        // Apply only the fields explicitly provided; empty strings clear the
        // value, null means "don't change". Frontend always sends all three.
        user.Specialization = string.IsNullOrWhiteSpace(request.Specialization) ? null : request.Specialization.Trim();
        user.Degree         = string.IsNullOrWhiteSpace(request.Degree)         ? null : request.Degree.Trim();
        user.LicenseNo      = string.IsNullOrWhiteSpace(request.LicenseNo)      ? null : request.LicenseNo.Trim();

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated clinical credentials for user {UserId}: Specialization={Specialization} Degree={Degree} LicenseNo={LicenseNo}",
            user.UserId, user.Specialization, user.Degree, user.LicenseNo);

        return new UpdateClinicalCredentialsResponse(Success: true);
    }
}
