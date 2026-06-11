using System.Linq;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Commands.SetHospitalModules;

public class SetHospitalModulesHandler : IRequestHandler<SetHospitalModulesCommand, SetHospitalModulesResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IUserContext _userContext;
    private readonly IModuleEntitlementService _modules;

    public SetHospitalModulesHandler(IApplicationDbContext db, IUserContext userContext, IModuleEntitlementService modules)
    {
        _db = db;
        _userContext = userContext;
        _modules = modules;
    }

    public async Task<SetHospitalModulesResponse> Handle(SetHospitalModulesCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty)
            return new SetHospitalModulesResponse { Success = false, Error = "No active center in the session." };

        var requested = ModuleConstants.Parse(request.Modules);
        var valid = new[] { ModuleConstants.Ris, ModuleConstants.Pacs };
        if (requested.Count == 0 || requested.Any(m => !valid.Contains(m, StringComparer.OrdinalIgnoreCase)))
            return new SetHospitalModulesResponse { Success = false, Error = "Modules must be a non-empty combination of RIS and/or PACS." };

        var sub = await _db.HospitalSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (sub == null)
            return new SetHospitalModulesResponse { Success = false, Error = "No subscription found for this center." };

        var current = ModuleConstants.Parse(sub.Modules);
        var pacsWas = current.Contains(ModuleConstants.Pacs);
        var pacsNow = requested.Contains(ModuleConstants.Pacs);

        if (pacsWas && !pacsNow)
            // Start the grace clock — but don't reset it if a removal is already
            // mid-grace (e.g. an admin re-saves "RIS" again).
            sub.PacsRemovedAt ??= DateTime.UtcNow;
        else if (!pacsWas && pacsNow)
            // Re-subscribed to PACS — cancel the grace / pending auto-delete.
            sub.PacsRemovedAt = null;

        // Normalised, deduped, ordered for stable storage ("PACS,RIS").
        sub.Modules = string.Join(",", requested.OrderBy(m => m));
        await _db.SaveChangesAsync(cancellationToken);
        _modules.Invalidate(hospitalId);

        return new SetHospitalModulesResponse
        {
            Success = true,
            Modules = sub.Modules,
            PacsRemovedAt = sub.PacsRemovedAt,
        };
    }
}
