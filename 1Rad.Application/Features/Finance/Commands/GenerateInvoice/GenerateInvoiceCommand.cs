using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.GenerateInvoice;

public record GenerateInvoiceCommand : IRequest<Guid>
{
    public Guid? AppointmentId { get; init; }
    public Guid PatientId { get; init; }
    public List<InvoiceItemDto> Items { get; init; } = new();
}

public record InvoiceItemDto(string Description, decimal Amount, int Quantity);

public class GenerateInvoiceCommandHandler : IRequestHandler<GenerateInvoiceCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public GenerateInvoiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(GenerateInvoiceCommand request, CancellationToken cancellationToken)
    {
        var patient = await _context.Patients
            .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);
        
        if (patient == null) throw new Exception("Patient not found.");

        var invoice = new Invoice
        {
            AppointmentId = request.AppointmentId,
            PatientId = request.PatientId,
            PatientName = patient.FullName,
            HospitalId = _context.UserContext.HospitalId,
            DisplayId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 4).ToUpper()}",
            TotalAmount = request.Items.Sum(x => x.Amount * x.Quantity),
            PaidAmount = 0,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        foreach (var item in request.Items)
        {
            invoice.Items.Add(new InvoiceItem
            {
                Description = item.Description,
                Amount = item.Amount,
                Quantity = item.Quantity
            });
        }

        _context.Invoices.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);

        return invoice.InvoiceId;
    }
}
