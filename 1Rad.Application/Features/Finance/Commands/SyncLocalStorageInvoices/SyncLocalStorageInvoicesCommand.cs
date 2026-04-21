using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.SyncLocalStorageInvoices;

public record SyncLocalStorageInvoicesCommand : IRequest<int>
{
    public List<LegacyInvoiceDto> Invoices { get; init; } = new();
}

public record LegacyInvoiceDto(
    string InvoiceId, 
    string PatientName, 
    decimal TotalAmount, 
    string Status, 
    DateTime CreatedAt, 
    List<LegacyItemDto> Items
);

public record LegacyItemDto(string Description, decimal Amount, int Quantity);

public class SyncLocalStorageInvoicesCommandHandler : IRequestHandler<SyncLocalStorageInvoicesCommand, int>
{
    private readonly IApplicationDbContext _context;

    public SyncLocalStorageInvoicesCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> Handle(SyncLocalStorageInvoicesCommand request, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (var legacy in request.Invoices)
        {
            // Avoid duplicates
            var exists = await _context.Invoices.AnyAsync(i => i.DisplayId == legacy.InvoiceId, cancellationToken);
            if (exists) continue;

            // Try to find patient by name in current hospital
            var patient = await _context.Patients
                .FirstOrDefaultAsync(p => p.FullName == legacy.PatientName && p.HospitalId == _context.UserContext.HospitalId, cancellationToken);
            
            // Skip if patient not found - don't create orphaned invoices
            if (patient == null) continue;

            var invoice = new Invoice
            {
                DisplayId = legacy.InvoiceId,
                PatientName = legacy.PatientName,
                TotalAmount = legacy.TotalAmount,
                PaidAmount = legacy.Status == "PAID" ? legacy.TotalAmount : 0,
                Status = legacy.Status,
                CreatedAt = legacy.CreatedAt,
                HospitalId = _context.UserContext.HospitalId,
                PatientId = patient.PatientId // ✅ Use actual patient ID instead of Guid.Empty
            };

            foreach (var item in legacy.Items)
            {
                invoice.Items.Add(new InvoiceItem
                {
                    Description = item.Description,
                    Amount = item.Amount,
                    Quantity = item.Quantity
                });
            }

            _context.Invoices.Add(invoice);
            count++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return count;
    }
}
