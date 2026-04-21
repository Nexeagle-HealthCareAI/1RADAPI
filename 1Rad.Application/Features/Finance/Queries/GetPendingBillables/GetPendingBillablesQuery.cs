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
        // 1. Get all appointments for the patient that don't have an invoice yet
        var unbilledAppointments = await _context.Appointments
            .Where(a => a.PatientId == request.PatientId && a.Status != "CANCELLED")
            .Where(a => !_context.Invoices.Any(i => i.AppointmentId == a.AppointmentId && i.Status != "CANCELLED"))
            .ToListAsync(cancellationToken);

        // 2. Map and try to find prices from Service Registry
        var registry = await _context.ServiceCharges.ToListAsync(cancellationToken);

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
}
