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
        // Defensive Check: Ensure the synchronization is being performed within a valid institutional context.
        if (_context.UserContext.HospitalId == Guid.Empty)
        {
            return 0;
        }

        int count = 0;
        foreach (var legacy in request.Invoices)
        {
            // Avoid duplicates - Standard property-based verification
            var exists = await _context.Invoices
                .AsNoTracking()
                .AnyAsync(i => i.InvoiceId == legacy.InvoiceId, cancellationToken);
            
            if (exists) continue;

            // Try to find patient by name in current hospital
            var patient = await _context.Patients
                .FirstOrDefaultAsync(p => p.FullName == legacy.PatientName && p.HospitalId == _context.UserContext.HospitalId, cancellationToken);
            
            // Skip if patient not found - don't create orphaned invoices
            if (patient == null) continue;

            // legacy.TotalAmount is the one trustworthy figure here (it's the
            // actual historical bill) — the old localStorage shape carries no
            // discount breakdown, so we can't derive Total the normal way
            // (Common/InvoiceTotals.cs) without discarding it. Instead, back-fill
            // Gross/Discount so all three stay mutually consistent: previously
            // this left GrossAmount/DiscountAmount at 0, silently understating
            // this invoice everywhere GrossAmount is read (e.g. the Analytics
            // tab's revenue totals).
            var itemsSubtotal = legacy.Items.Sum(i => i.Amount * i.Quantity);
            var grossAmount = itemsSubtotal > 0 ? itemsSubtotal : legacy.TotalAmount;

            var invoice = new Invoice
            {
                InvoiceId = legacy.InvoiceId,
                GrossAmount = grossAmount,
                DiscountAmount = Math.Max(0, grossAmount - legacy.TotalAmount),
                TotalAmount = legacy.TotalAmount,
                PaidAmount = legacy.Status == "PAID" ? legacy.TotalAmount : 0,
                Status = legacy.Status,
                CreatedAt = legacy.CreatedAt,
                HospitalId = _context.UserContext.HospitalId,
                PatientId = patient.PatientId
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
