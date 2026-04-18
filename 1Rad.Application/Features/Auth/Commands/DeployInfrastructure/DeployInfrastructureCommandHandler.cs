using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Domain.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public class DeployInfrastructureCommandHandler : IRequestHandler<DeployInfrastructureCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<DeployInfrastructureCommandHandler> _logger;

    public DeployInfrastructureCommandHandler(IApplicationDbContext context, ILogger<DeployInfrastructureCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> Handle(DeployInfrastructureCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deploying Infrastructure for User: {UserId}", request.UserId);

        try
        {
            // 1. Verify User exists and is in Pending state
            var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("Deployment attempt for non-existent user: {UserId}", request.UserId);
                return (false, "User not found.");
            }

            if (user.Status != UserStatus.Pending)
            {
                _logger.LogWarning("User {UserId} is not in Pending status. Current status: {Status}", request.UserId, user.Status);
                return (false, "Registration is already complete or invalid.");
            }

            // 2. Create Group
            _logger.LogDebug("Creating Hospital Group: {ChainName}", request.ChainName);
            var group = new HospitalGroup
            {
                GroupName = request.ChainName
            };
            _context.HospitalGroups.Add(group);

            // 3. Create Hospital
            _logger.LogDebug("Creating Hospital: {HospitalName}", request.HospitalName);
            var hospital = new Hospital
            {
                GroupId = group.GroupId,
                HospitalName = request.HospitalName,
                HospitalAddress = request.HospitalAddress,
                GSTIN = request.GSTINNumber,
                RegistrationNumber = request.RegistrationNumber,
                PAN = request.PANNumber,
                NABHNumber = request.NABHNumber,
                Status = "Active"
            };
            _context.Hospitals.Add(hospital);

            // 4. Map Authority
            _logger.LogDebug("Mapping User {UserId} to Hospital {HospitalId} with Role {RoleName}", user.UserId, hospital.HospitalId, request.RoleName);
            
            var role = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == request.RoleName, cancellationToken);
            if (role == null)
            {
                _logger.LogWarning("Deployment failed: Role {RoleName} not found.", request.RoleName);
                return (false, "Specified role not found.");
            }

            var mapping = new UserHospitalMapping
            {
                UserId = user.UserId,
                HospitalId = hospital.HospitalId,
                IsDefault = true
            };
            mapping.Roles.Add(role);
            _context.UserHospitalMappings.Add(mapping);

            // 5. Promote User & Standardize Clinical Data
            _logger.LogInformation("Promoting User {UserId} to Active status.", user.UserId);
            user.Status = UserStatus.Active;
            user.IsVerified = true;
            user.Specialization = request.Specialization;
            user.Degree = request.Degree;
            user.LicenseNo = request.LicenseNo;

            // 6. Trigger Domain Event
            hospital.AddDomainEvent(new HospitalRegisteredEvent(user, hospital));

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Infrastructure deployment complete for User: {UserId}, Hospital: {HospitalId}", user.UserId, hospital.HospitalId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Infrastructure deployment failed for user {UserId}", request.UserId);
            return (false, "Critical error during deployment. Please contact support.");
        }
    }
}
