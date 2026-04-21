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

            var invoice = new Invoice
            {
                DisplayId = legacy.InvoiceId,
                PatientName = legacy.PatientName,
                TotalAmount = legacy.TotalAmount,
                PaidAmount = legacy.Status == "PAID" ? legacy.TotalAmount : 0,
                Status = legacy.Status,
                CreatedAt = legacy.CreatedAt,
                HospitalId = _context.UserContext.HospitalId,
                // We don't have Guid IDs for legacy appointments/patients easily here, 
                // so we rely on PatientName for audit for now. 
                // A better approach would match by Name/Mobile but that's risky for auto-sync.
                PatientId = Guid.Empty // Or search for a match
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
