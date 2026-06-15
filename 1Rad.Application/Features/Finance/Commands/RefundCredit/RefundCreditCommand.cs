using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.RefundCredit;

// Return part/all of a patient's credit-wallet balance as cash. Direct (no admin
// approval — per the agreed flow). Writes a REFUND ledger row, lowering the
// wallet. Hospital-scoped.
public record RefundCreditCommand : IRequest<RefundCreditResult>
{
    public Guid PatientId { get; init; }
    public decimal Amount { get; init; }
    public string PaymentMethod { get; init; } = "CASH";
    public string? Remarks { get; init; }
}

public record RefundCreditResult(bool Success, decimal NewBalance, string? Error);

public class RefundCreditCommandHandler : IRequestHandler<RefundCreditCommand, RefundCreditResult>
{
    private readonly IApplicationDbContext _context;
    public RefundCreditCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<RefundCreditResult> Handle(RefundCreditCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");
        if (request.Amount <= 0)
            return new RefundCreditResult(false, 0m, "Refund amount must be positive.");

        var txns = await _context.CreditTransactions
            .Where(c => c.PatientId == request.PatientId && c.DeletedAt == null)
            .ToListAsync(ct);

        var balance = txns.Sum(t => t.Type == "ADVANCE" ? t.Amount : -t.Amount);
        if (request.Amount > balance + 0.01m)
            return new RefundCreditResult(false, balance, $"Refund (₹{request.Amount:0.##}) exceeds the patient's available credit (₹{balance:0.##}).");

        var name = txns.OrderByDescending(t => t.CreatedAt).Select(t => t.PatientName).FirstOrDefault() ?? string.Empty;
        var amount = Math.Round(request.Amount, 2);

        _context.CreditTransactions.Add(new CreditTransaction
        {
            HospitalId = hospitalId,
            PatientId = request.PatientId,
            PatientName = name,
            Type = "REFUND",
            Amount = amount,
            PaymentMethod = request.PaymentMethod,
            Remarks = request.Remarks,
            CreatedByUserId = _context.UserContext.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await _context.SaveChangesAsync(ct);
        return new RefundCreditResult(true, balance - amount, null);
    }
}
