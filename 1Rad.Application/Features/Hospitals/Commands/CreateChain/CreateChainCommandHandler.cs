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

    public CreateChainCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<CreateChainResponse> Handle(CreateChainCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Validate User
            var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
            if (user == null) return new CreateChainResponse { Success = false, Error = "User identity not found." };

            // 2. Create the Chain (Group)
            var group = new HospitalGroup
            {
                GroupName = request.ChainName,
                CreatedAt = DateTime.UtcNow
            };
            _context.HospitalGroups.Add(group);

            // 3. Create the Primary Center (Hospital)
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
                // Fallback to any role containing admin if AdminDoctor is missing
                adminRole = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName.Contains("Admin"), cancellationToken);
            }

            if (adminRole == null) return new CreateChainResponse { Success = false, Error = "Administrative role protocols not found in system baseline." };

            var mapping = new UserHospitalMapping
            {
                UserId = user.UserId,
                HospitalId = hospital.HospitalId,
                IsDefault = false // Maintain current default, but link to new node
            };
            mapping.Roles.Add(adminRole);
            _context.UserHospitalMappings.Add(mapping);

            await _context.SaveChangesAsync(cancellationToken);

            return new CreateChainResponse { Success = true, HospitalId = hospital.HospitalId };
        }
        catch (Exception ex)
        {
            return new CreateChainResponse { Success = false, Error = $"Infrastructure deployment failed: {ex.Message}" };
        }
    }
}
