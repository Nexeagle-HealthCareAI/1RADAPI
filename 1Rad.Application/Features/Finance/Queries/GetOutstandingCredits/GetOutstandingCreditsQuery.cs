using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Queries.GetOutstandingCredits;

// Patients who currently hold a positive credit balance (advances awaiting a
// refund or carry-forward). Drives the "Advances & refunds" list.
public record GetOutstandingCreditsQuery : IRequest<List<OutstandingCreditDto>>;

public record OutstandingCreditDto(Guid PatientId, string PatientName, decimal Balance, DateTime LastActivity);

public class GetOutstandingCreditsQueryHandler : IRequestHandler<GetOutstandingCreditsQuery, List<OutstandingCreditDto>>
{
    private readonly IApplicationDbContext _context;
    public GetOutstandingCreditsQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<OutstandingCreditDto>> Handle(GetOutstandingCreditsQuery request, CancellationToken ct)
    {
        // Hospital scope comes from the global query filter. Materialise then
        // fold per patient — the ledger is small relative to the dataset.
        var txns = await _context.CreditTransactions
            .Where(c => c.DeletedAt == null)
            .Select(c => new { c.PatientId, c.PatientName, c.Type, c.Amount, c.CreatedAt })
            .ToListAsync(ct);

        return txns
            .GroupBy(c => c.PatientId)
            .Select(g => new OutstandingCreditDto(
                g.Key,
                g.OrderByDescending(x => x.CreatedAt).Select(x => x.PatientName).FirstOrDefault() ?? string.Empty,
                g.Sum(x => x.Type == "ADVANCE" ? x.Amount : -x.Amount),
                g.Max(x => x.CreatedAt)))
            .Where(d => d.Balance > 0.01m)
            .OrderByDescending(d => d.Balance)
            .ToList();
    }
}
