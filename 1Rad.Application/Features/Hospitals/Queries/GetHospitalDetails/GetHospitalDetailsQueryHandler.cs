using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;

public class GetHospitalDetailsQueryHandler : IRequestHandler<GetHospitalDetailsQuery, HospitalDetailsDto>
{
    private readonly IApplicationDbContext _context;

    public GetHospitalDetailsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<HospitalDetailsDto> Handle(GetHospitalDetailsQuery request, CancellationToken cancellationToken)
    {
        var hospital = await _context.Hospitals
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.HospitalId == request.HospitalId, cancellationToken);

        if (hospital == null) return null;

        return new HospitalDetailsDto(
            hospital.HospitalId,
            hospital.HospitalName,
            hospital.HospitalAddress,
            hospital.GSTIN,
            hospital.RegistrationNumber,
            hospital.PAN,
            hospital.NABHNumber,
            hospital.Status);
    }
}
