using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Patients.Commands.CreatePatient;

public record CreatePatientCommand(
    string FullName,
    string Mobile,
    string Age,
    string Gender,
    string Village,
    string District,
    string Address,
    string SourceOfInfo,
    Guid? ReferrerId = null
) : IRequest<Guid>;

public class CreatePatientCommandHandler : IRequestHandler<CreatePatientCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreatePatientCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(CreatePatientCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;

        // 1. Check for Deduplication (Name + Mobile)
        var existingPatient = await _context.Patients
            .FirstOrDefaultAsync(p => p.FullName == request.FullName && 
                                     p.Mobile == request.Mobile && 
                                     p.HospitalId == hospitalId, cancellationToken);

        if (existingPatient != null)
        {
            // 2. Auto-Update existing record with new details
            existingPatient.Age = request.Age;
            existingPatient.Gender = request.Gender;
            existingPatient.Village = request.Village;
            existingPatient.District = request.District;
            existingPatient.Address = request.Address;
            existingPatient.SourceOfInfo = request.SourceOfInfo;
            existingPatient.ReferrerId = request.ReferrerId;

            await _context.SaveChangesAsync(cancellationToken);
            return existingPatient.PatientId;
        }

        // 3. Create New Patient if not found
        var count = await _context.Patients
            .CountAsync(p => p.HospitalId == hospitalId, cancellationToken);
            
        var patient = new Patient
        {
            FullName = request.FullName,
            Mobile = request.Mobile,
            Age = request.Age,
            Gender = request.Gender,
            Village = request.Village,
            District = request.District,
            Address = request.Address,
            SourceOfInfo = request.SourceOfInfo,
            ReferrerId = request.ReferrerId,
            PatientIdentifier = $"PTID{(count + 1):D8}",
            HospitalId = hospitalId
        };

        _context.Patients.Add(patient);
        await _context.SaveChangesAsync(cancellationToken);

        return patient.PatientId;
    }
}
