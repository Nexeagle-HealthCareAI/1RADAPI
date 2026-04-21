using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;

public class UpdateHospitalDetailsCommandHandler : IRequestHandler<UpdateHospitalDetailsCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<UpdateHospitalDetailsCommandHandler> _logger;

    public UpdateHospitalDetailsCommandHandler(IApplicationDbContext context, ILogger<UpdateHospitalDetailsCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> Handle(UpdateHospitalDetailsCommand request, CancellationToken cancellationToken)
    {
        var hospital = await _context.Hospitals
            .FirstOrDefaultAsync(h => h.HospitalId == request.HospitalId, cancellationToken);

        if (hospital == null)
        {
            return (false, "Hospital not found.");
        }

        hospital.HospitalName = request.HospitalName;
        hospital.HospitalAddress = request.HospitalAddress;
        hospital.GSTIN = request.GSTIN;
        hospital.RegistrationNumber = request.RegistrationNumber;
        hospital.PAN = request.PAN;
        hospital.NABHNumber = request.NABHNumber;
        hospital.IsAutoBillingEnabled = request.IsAutoBillingEnabled;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update hospital {HospitalId}", request.HospitalId);
            return (false, "Storage failure during update.");
        }
    }
}
