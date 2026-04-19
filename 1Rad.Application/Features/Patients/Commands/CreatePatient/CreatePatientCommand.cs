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
    string SourceOfInfo
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
            PatientIdentifier = $"PTID{(count + 1):D8}", // Readable hospital-scoped sequence
            HospitalId = hospitalId
        };

        _context.Patients.Add(patient);
        await _context.SaveChangesAsync(cancellationToken);

        return patient.PatientId;
    }
}
