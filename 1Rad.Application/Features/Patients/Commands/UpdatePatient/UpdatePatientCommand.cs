using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Patients.Commands.UpdatePatient;

public record UpdatePatientCommand(
    Guid PatientId,
    string FullName,
    string Mobile,
    string Age,
    string Gender,
    string Village,
    string District,
    string Address,
    string SourceOfInfo,
    Guid? ReferrerId = null
) : IRequest<bool>;

public class UpdatePatientCommandHandler : IRequestHandler<UpdatePatientCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdatePatientCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdatePatientCommand request, CancellationToken cancellationToken)
    {
        var patient = await _context.Patients
            .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);

        if (patient == null) return false;

        patient.FullName = request.FullName;
        patient.Mobile = request.Mobile;
        patient.Age = request.Age;
        patient.Gender = request.Gender;
        patient.Village = request.Village;
        patient.District = request.District;
        patient.Address = request.Address;
        patient.SourceOfInfo = request.SourceOfInfo;
        patient.ReferrerId = request.ReferrerId;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
