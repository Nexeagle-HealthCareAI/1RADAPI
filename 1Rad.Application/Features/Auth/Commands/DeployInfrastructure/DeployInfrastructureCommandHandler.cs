using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Domain.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public class DeployInfrastructureCommandHandler : IRequestHandler<DeployInfrastructureCommand, DeployInfrastructureResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<DeployInfrastructureCommandHandler> _logger;

    public DeployInfrastructureCommandHandler(IApplicationDbContext context, ILogger<DeployInfrastructureCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<DeployInfrastructureResponse> Handle(DeployInfrastructureCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deploying Infrastructure for User: {UserId}", request.UserId);

        try
        {
            // 1. Verify User exists and is in Pending state
            var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("Deployment attempt for non-existent user: {UserId}", request.UserId);
                return new DeployInfrastructureResponse { Success = false, Error = "Clinical identity not found.", ErrorCode = "USER_NOT_FOUND" };
            }

            if (user.Status != UserStatus.Pending)
            {
                _logger.LogWarning("User {UserId} is not in Pending status. Current status: {Status}", request.UserId, user.Status);
                return new DeployInfrastructureResponse { Success = false, Error = "Registration is already complete or invalid for this identity.", ErrorCode = "ALREADY_REGISTERED" };
            }

            // ... (Creation logic remains same)
            // 2. Create Group
            var group = new HospitalGroup { GroupName = request.ChainName };
            _context.HospitalGroups.Add(group);

            // 3. Create Hospital
            var hospital = new Hospital {
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
            var role = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == request.RoleName, cancellationToken);
            if (role == null)
            {
                _logger.LogWarning("Deployment failed: Role {RoleName} not found.", request.RoleName);
                return new DeployInfrastructureResponse { Success = false, Error = $"The clinical role '{request.RoleName}' is not recognized in the system baseline.", ErrorCode = "ROLE_NOT_FOUND" };
            }

            var mapping = new UserHospitalMapping { UserId = user.UserId, HospitalId = hospital.HospitalId, IsDefault = true };
            mapping.Roles.Add(role);
            _context.UserHospitalMappings.Add(mapping);

            // 5. Promote User
            user.Status = UserStatus.Active;
            user.IsVerified = true;
            user.Specialization = request.Specialization;
            user.Degree = request.Degree;
            user.LicenseNo = request.LicenseNo;

            await _context.SaveChangesAsync(cancellationToken);

            // Auto-activate 14-day free trial for newly registered hospital
            var trialSubscription = new HospitalSubscription
            {
                HospitalId = hospital.HospitalId,
                PlanId = null,
                IsTrial = true,
                BillingCycle = "Trial",
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddDays(14),
                Status = "Active",
                IsLocked = false
            };
            _context.HospitalSubscriptions.Add(trialSubscription);
            await _context.SaveChangesAsync(cancellationToken);

            return new DeployInfrastructureResponse { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Infrastructure deployment failed for user {UserId}", request.UserId);
            return new DeployInfrastructureResponse { Success = false, Error = "Critical infrastructure failure during deployment. Center command notified.", ErrorCode = "INTERNAL_ERROR" };
        }
    }
}
