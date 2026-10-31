using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.RefundPayment;

/// <summary>
/// Give money back on a bill (review B6). Writes a REFUND row dated today with the
/// mode and the user; the original payment rows are never touched. Money that came
/// from the patient's advance always returns to the advance.
/// </summary>
public record RefundPaymentCommand(Guid InvoiceId, decimal Amount, string Mode, string Reason)
    : IRequest<RefundPaymentResult>;

public record RefundPaymentResult(decimal Refunded, decimal ToWallet, decimal PaidOut, decimal PaidAmount, string Status);

public class RefundPaymentCommandHandler : IRequestHandler<RefundPaymentCommand, RefundPaymentResult>
{
    private readonly IApplicationDbContext _context;
    public RefundPaymentCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<RefundPaymentResult> Handle(RefundPaymentCommand request, CancellationToken ct)
    {
        if (request.Amount <= 0)
            throw new ValidationException("Enter the amount to refund.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ValidationException("Say why the money is being returned.");
        var mode = (request.Mode ?? string.Empty).Trim().ToUpperInvariant();
        if (!PaymentLedger.RefundModes.Contains(mode))
            throw new ValidationException("Choose how the money goes back: cash, UPI, card, or the patient's advance.");

        var invoice = await _context.Invoices
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.HospitalId == _context.UserContext.HospitalId && i.DeletedAt == null, ct)
            ?? throw new NotFoundException("Bill not found.");
        if (string.Equals(invoice.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleViolationException("This bill is cancelled; its money has already been returned.", "INVOICE_CLOSED");

        var outcome = await PaymentLedger.RefundAsync(_context, invoice, request.Amount, mode, request.Reason, ct);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This bill was updated by someone else a moment ago. Reopen it and try the refund again.");
        }
        return new RefundPaymentResult(outcome.Refunded, outcome.ToWallet, outcome.PaidOut, invoice.PaidAmount, invoice.Status);
    }
}
