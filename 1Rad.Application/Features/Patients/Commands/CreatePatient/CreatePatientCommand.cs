using _1Rad.Application.Common;
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
        var mobile = (request.Mobile ?? string.Empty).Trim();
        var normalizedName = NameNormalizer.Normalize(request.FullName);

        // 1. Deduplication SAFETY NET — only auto-merge on a strong, unambiguous
        //    signal: the SAME phone number AND the same normalised name (collapses
        //    casing/spacing/honorific/punctuation variants). When the mobile is
        //    blank we never auto-merge on name alone — namesakes without a phone
        //    are different people; the client-side fuzzy prompt + the operator's
        //    "This is them" confirmation handle that case instead.
        Patient? existingPatient = null;
        if (!string.IsNullOrWhiteSpace(mobile))
        {
            existingPatient = await _context.Patients
                .FirstOrDefaultAsync(p => p.HospitalId == hospitalId &&
                                          p.Mobile == mobile &&
                                          (p.NameNormalized == normalizedName || p.FullName == request.FullName),
                                     cancellationToken);
        }

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
            existingPatient.NameNormalized = normalizedName;

            await _context.SaveChangesAsync(cancellationToken);
            return existingPatient.PatientId;
        }

        // 3. Create New Patient if not found
        var count = await _context.Patients
            .CountAsync(p => p.HospitalId == hospitalId, cancellationToken);
            
        var patient = new Patient
        {
            FullName = NameNormalizer.Upper(request.FullName),
            Mobile = mobile,
            NameNormalized = normalizedName,
            Age = request.Age,
            Gender = request.Gender,
            Village = NameNormalizer.Upper(request.Village),
            District = NameNormalizer.Upper(request.District),
            Address = NameNormalizer.Upper(request.Address),
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
