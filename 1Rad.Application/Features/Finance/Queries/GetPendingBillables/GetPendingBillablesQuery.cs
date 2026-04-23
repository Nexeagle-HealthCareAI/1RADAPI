using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Queries.GetPendingBillables;

public record GetPendingBillablesQuery : IRequest<List<PendingBillableDto>>
{
    public Guid PatientId { get; init; }
}

public class PendingBillableDto
{
    public Guid AppointmentId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime DateTime { get; set; }
    public decimal? Amount { get; set; }
}

public class GetPendingBillablesQueryHandler : IRequestHandler<GetPendingBillablesQuery, List<PendingBillableDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPendingBillablesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PendingBillableDto>> Handle(GetPendingBillablesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<PendingBillableDto>();
            }

            // Validate patient ID
            if (request.PatientId == Guid.Empty)
            {
                throw new ArgumentException("Patient ID is required.", nameof(request.PatientId));
            }

            // Verify patient exists and belongs to the hospital
            var patient = await _context.Patients
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PatientId == request.PatientId && p.HospitalId == _context.UserContext.HospitalId, cancellationToken);

            if (patient == null)
            {
                throw new KeyNotFoundException($"Patient with ID '{request.PatientId}' not found or does not belong to your hospital.");
            }

            // Optimization: Execute the 'Already Invoiced' check as a SQL subquery rather than fetching IDs into memory.
            // This prevents the 'Parameter Limit' crash in SQL Server as the invoice count grows.
            var unbilledAppointments = await _context.Appointments
                .AsNoTracking()
                .Where(a => a.PatientId == request.PatientId && 
                            a.Status != "CANCELLED" && 
                            a.HospitalId == _context.UserContext.HospitalId)
                .Where(a => !_context.Invoices.Any(i => i.AppointmentId == a.AppointmentId && i.Status != "CANCELLED"))
                .ToListAsync(cancellationToken);

            // Map and try to find prices from Service Registry
            var registry = await _context.ServiceCharges.AsNoTracking().ToListAsync(cancellationToken);

            var result = unbilledAppointments.Select(a => {
                var price = registry.FirstOrDefault(r => 
                    r.Modality.Equals(a.Modality, StringComparison.OrdinalIgnoreCase) && 
                    r.ServiceName.Equals(a.Service, StringComparison.OrdinalIgnoreCase));
                
                return new PendingBillableDto
                {
                    AppointmentId = a.AppointmentId,
                    Service = a.Service,
                    Modality = a.Modality,
                    DateTime = a.DateTime,
                    Amount = price?.Amount
                };
            }).ToList();

            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve pending billables: {ex.Message}", ex);
        }
    }
}
