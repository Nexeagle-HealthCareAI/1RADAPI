using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Hospitals.Commands.CreateChain;

public class CreateChainCommandHandler : IRequestHandler<CreateChainCommand, CreateChainResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public CreateChainCommandHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<CreateChainResponse> Handle(CreateChainCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Validate User
            var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null) return new CreateChainResponse { Success = false, Error = "User identity not found." };

            // 2. Identify or Create the Institutional Group (Chain)
            var currentGroupId = _userContext.GroupId;
            HospitalGroup group = null;

            if (currentGroupId.HasValue)
            {
                group = await _context.HospitalGroups.FindAsync(new object[] { currentGroupId.Value }, cancellationToken);
            }

            if (group == null)
            {
                // Fallback: Check if user belongs to any hospital that has a group
                var existingMapping = await _context.UserHospitalMappings
                    .Include(m => m.Hospital)
                    .Where(m => m.UserId == request.UserId && m.Hospital.GroupId != null)
                    .FirstOrDefaultAsync(cancellationToken);

                if (existingMapping != null)
                {
                    group = await _context.HospitalGroups.FindAsync(new object[] { existingMapping.Hospital.GroupId!.Value }, cancellationToken);
                }
            }

            if (group != null)
            {
                // Institutional Brand Override: Update existing group name as specified by user
                group.GroupName = request.ChainName;
                _context.HospitalGroups.Update(group);
            }
            else
            {
                // Initial Root: Create new hospital group for the first-time chain
                group = new HospitalGroup
                {
                    GroupName = request.ChainName,
                    CreatedAt = DateTime.UtcNow
                };
                _context.HospitalGroups.Add(group);
            }

            // Ensure changes to group (new or updated) are tracked
            await _context.SaveChangesAsync(cancellationToken);

            // 3. Create the Centre node (Hospital)
            var hospital = new Hospital
            {
                GroupId = group.GroupId,
                HospitalName = request.HospitalName,
                HospitalAddress = request.HospitalAddress,
                GSTIN = request.GSTIN,
                RegistrationNumber = request.RegistrationNumber,
                PAN = request.PAN,
                NABHNumber = request.NABHNumber,
                Status = "Active",
                CreatedAt = DateTime.UtcNow
            };
            _context.Hospitals.Add(hospital);

            // 4. Map User to new node as AdminDoctor
            var adminRole = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == "AdminDoctor", cancellationToken);
            if (adminRole == null)
            {
                adminRole = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName.Contains("Admin"), cancellationToken);
            }

            if (adminRole == null) return new CreateChainResponse { Success = false, Error = "Administrative role protocols not found." };

            var mapping = new UserHospitalMapping
            {
                UserId = user.UserId,
                HospitalId = hospital.HospitalId,
                IsDefault = false 
            };
            mapping.Roles.Add(adminRole);
            _context.UserHospitalMappings.Add(mapping);

            // 5. Add 15-day Trial Subscription
            var trialSubscription = new HospitalSubscription
            {
                HospitalId = hospital.HospitalId,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddDays(15),
                IsTrial = true,
                Status = "Active",
                CreatedAt = DateTime.UtcNow
            };
            _context.HospitalSubscriptions.Add(trialSubscription);

            await _context.SaveChangesAsync(cancellationToken);

            return new CreateChainResponse { Success = true, HospitalId = hospital.HospitalId };
        }
        catch (Exception ex)
        {
            return new CreateChainResponse { Success = false, Error = $"Infrastructure deployment failed: {ex.Message}" };
        }
    }
}
