using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.ApplyCredit;

// Carry a patient's wallet credit forward onto a later invoice: records a Payment
// (method = "ADVANCE", so cash reports can tell it apart from fresh cash) plus an
// APPLIED ledger row that lowers the wallet. Amount is capped at min(amount owed
// on the invoice, wallet balance). Null Amount = apply the maximum possible.
public record ApplyCreditCommand : IRequest<ApplyCreditResult>
{
    public Guid InvoiceId { get; init; }
    public decimal? Amount { get; init; }
}

public record ApplyCreditResult(bool Success, decimal Applied, decimal NewWalletBalance, string? Error);

public class ApplyCreditCommandHandler : IRequestHandler<ApplyCreditCommand, ApplyCreditResult>
{
    private readonly IApplicationDbContext _context;
    public ApplyCreditCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<ApplyCreditResult> Handle(ApplyCreditCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var invoice = await _context.Invoices
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == hospitalId, ct);
        if (invoice == null)
            return new ApplyCreditResult(false, 0m, 0m, "Invoice not found.");
        if (invoice.Status == "CANCELLED")
            return new ApplyCreditResult(false, 0m, 0m, "Invoice is cancelled.");

        var owed = invoice.TotalAmount - invoice.PaidAmount;
        if (owed <= 0.009m)
            return new ApplyCreditResult(false, 0m, 0m, "This invoice has nothing left to pay.");

        var txns = await _context.CreditTransactions
            .Where(c => c.PatientId == invoice.PatientId && c.DeletedAt == null)
            .ToListAsync(ct);
        var balance = txns.Sum(t => t.Type == "ADVANCE" ? t.Amount : -t.Amount);
        if (balance <= 0.009m)
            return new ApplyCreditResult(false, 0m, balance, "This patient has no advance credit to apply.");

        var requested = request.Amount ?? Math.Min(owed, balance);
        var toApply = Math.Round(Math.Min(Math.Min(requested, owed), balance), 2);
        if (toApply <= 0.009m)
            return new ApplyCreditResult(false, 0m, balance, "Nothing to apply.");

        // Settle the invoice from the wallet — a Payment tagged ADVANCE so it
        // counts as revenue on this invoice but cash reports can exclude it
        // (the cash was received earlier when the advance was taken).
        _context.Payments.Add(new Payment
        {
            InvoiceId = invoice.Id,
            Amount = toApply,
            PaymentMethod = "ADVANCE",
            CreatedAt = DateTime.UtcNow,
            HospitalId = hospitalId,
        });
        invoice.PaidAmount += toApply;
        if (invoice.PaidAmount >= invoice.TotalAmount - 0.01m)
        {
            invoice.Status = "PAID";
            invoice.PaidAt = DateTime.UtcNow;
        }
        else if (invoice.PaidAmount > 0)
        {
            invoice.Status = "PARTIAL";
        }

        _context.CreditTransactions.Add(new CreditTransaction
        {
            HospitalId = hospitalId,
            PatientId = invoice.PatientId,
            PatientName = invoice.PatientName ?? string.Empty,
            Type = "APPLIED",
            Amount = toApply,
            InvoiceId = invoice.Id,
            InvoiceDisplayId = invoice.InvoiceId,
            PaymentMethod = "ADVANCE",
            CreatedByUserId = _context.UserContext.UserId,
            Remarks = "Advance applied to invoice",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await _context.SaveChangesAsync(ct);
        return new ApplyCreditResult(true, toApply, balance - toApply, null);
    }
}
