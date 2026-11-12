using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
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
    private readonly ISubscriptionLimitsService _limits;

    public CreateChainCommandHandler(IApplicationDbContext context, IUserContext userContext, ISubscriptionLimitsService limits)
    {
        _context = context;
        _userContext = userContext;
        _limits = limits;
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

            // Enforce the plan's site cap (centers in the group) before adding
            // another center. Resolved from the acting center's subscription.
            if (_userContext.HospitalId != Guid.Empty)
            {
                var sites = await _limits.GetSiteLimitAsync(_userContext.HospitalId, cancellationToken);
                if (sites.AtLimit)
                    return new CreateChainResponse { Success = false, ErrorCode = "SITE_LIMIT_REACHED", Error = $"Your plan includes {sites.Max} site(s) ({sites.Current} in use). Upgrade your plan to add another centre." };
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

            // Resolve the chosen product package (SKU) for THIS new centre. Only
            // RIS/PACS are valid codes; an empty/garbage value defaults to the full
            // product so a bad client never locks the new centre out of everything.
            // (Mirrors DeployInfrastructure — modules are per-centre.)
            var chosen = ModuleConstants.Parse(request.Modules);
            var valid = new[] { ModuleConstants.Ris, ModuleConstants.Pacs };
            var modules = (chosen.Count > 0 && chosen.All(m => valid.Contains(m, StringComparer.OrdinalIgnoreCase)))
                ? string.Join(",", chosen.OrderBy(m => m))
                : ModuleConstants.DefaultModules;

            // 5. Add 14-day Trial Subscription
            var trialSubscription = new HospitalSubscription
            {
                HospitalId = hospital.HospitalId,
                PlanId = null,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddDays(14),
                IsTrial = true,
                BillingCycle = "Trial",
                Modules = modules,
                Status = "Active",
                IsLocked = false,
                CreatedAt = DateTime.UtcNow
            };
            _context.HospitalSubscriptions.Add(trialSubscription);

            await _context.SaveChangesAsync(cancellationToken);

            var response = new CreateChainResponse { Success = true, HospitalId = hospital.HospitalId };

            // Owner board: start with another centre's setup. The centre exists by now,
            // so a failure here is reported, not thrown — it can be copied again later.
            if (request.CopySetupFromHospitalId is Guid from && from != Guid.Empty)
            {
                try
                {
                    // A centre that follows another passes on its main centre, so links never chain.
                    var main = await _context.HospitalSettingsLinks.IgnoreQueryFilters().AsNoTracking()
                        .Where(l => l.HospitalId == from && (l.LinkBilling || l.LinkPrices))
                        .Select(l => (Guid?)l.SourceHospitalId).FirstOrDefaultAsync(cancellationToken);
                    if (request.KeepLinked && main is Guid m) from = m;
                    await _1Rad.Application.Features.Owner.OwnerAccess.RequireAsync(_context, user.UserId, new[] { from }, cancellationToken);
                    await _1Rad.Application.Features.Owner.ChainSetup.CopyAsync(_context, from, new[] { hospital.HospitalId },
                        billing: true, prices: true, keepLinked: request.KeepLinked, byUserId: user.UserId, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);
                    response.SetupCopied = true;
                }
                catch (Exception copyEx)
                {
                    response.SetupCopyError = copyEx.Message;
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            return new CreateChainResponse { Success = false, Error = $"Infrastructure deployment failed: {ex.Message}" };
        }
    }
}
