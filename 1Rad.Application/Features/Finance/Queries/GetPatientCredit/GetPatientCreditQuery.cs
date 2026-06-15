using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Queries.GetPatientCredit;

// A patient's wallet balance + recent ledger. Used by the payment drawer to offer
// "apply ₹X advance" and by the per-patient credit drill-down.
public record GetPatientCreditQuery(Guid PatientId) : IRequest<PatientCreditDto>;

public record PatientCreditDto(Guid PatientId, string PatientName, decimal Balance, List<CreditTxnDto> Transactions);

public record CreditTxnDto(
    Guid Id, string Type, decimal Amount, Guid? InvoiceId, string? InvoiceDisplayId,
    string? PaymentMethod, string? Remarks, DateTime CreatedAt);

public class GetPatientCreditQueryHandler : IRequestHandler<GetPatientCreditQuery, PatientCreditDto>
{
    private readonly IApplicationDbContext _context;
    public GetPatientCreditQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<PatientCreditDto> Handle(GetPatientCreditQuery request, CancellationToken ct)
    {
        var txns = await _context.CreditTransactions
            .Where(c => c.PatientId == request.PatientId && c.DeletedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var balance = txns.Sum(t => t.Type == "ADVANCE" ? t.Amount : -t.Amount);
        var name = txns.Select(t => t.PatientName).FirstOrDefault() ?? string.Empty;

        return new PatientCreditDto(
            request.PatientId,
            name,
            balance,
            txns.Select(t => new CreditTxnDto(
                t.Id, t.Type, t.Amount, t.InvoiceId, t.InvoiceDisplayId,
                t.PaymentMethod, t.Remarks, t.CreatedAt)).ToList());
    }
}
