using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.CreditNotes;

/// <summary>
/// Credit notes and refund vouchers issued on this centre's bills (review B6/B10).
/// They are raised automatically when a paid or part-paid bill is lowered,
/// cancelled or refunded (see CreditNote); this lists them, by IST issue date or
/// for one bill.
/// </summary>
public record GetCreditNotesQuery(DateTime? From, DateTime? To, Guid? InvoiceId) : IRequest<List<CreditNoteDto>>;

public record CreditNoteDto(
    Guid Id, string Kind, string Number, DateTime CreatedAt,
    Guid InvoiceId, string InvoiceNumber, DateTime InvoiceDate, string? PatientName,
    decimal Amount, decimal TaxableValue, decimal TaxAmount, decimal RefundedAmount, string? RefundModes,
    decimal BillTotalBefore, decimal BillTotalAfter, string Reason, string? IssuedBy);

public class GetCreditNotesQueryHandler : IRequestHandler<GetCreditNotesQuery, List<CreditNoteDto>>
{
    private readonly IApplicationDbContext _db;
    public GetCreditNotesQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<List<CreditNoteDto>> Handle(GetCreditNotesQuery q, CancellationToken ct)
    {
        var query = _db.CreditNotes.AsNoTracking().Where(c => c.HospitalId == _db.UserContext.HospitalId);
        if (q.InvoiceId.HasValue)
        {
            query = query.Where(c => c.InvoiceId == q.InvoiceId.Value);
        }
        else
        {
            var today = IstDateRange.ToIst(DateTime.UtcNow).Date;
            var from = (q.From ?? today.AddDays(-30)).Date;
            var to = (q.To ?? today).Date;
            if (to < from) (from, to) = (to, from);
            if ((to - from).TotalDays > 400) throw new ValidationException("Credit notes can be listed for at most 13 months at a time.");
            var start = IstDateRange.ToUtcStart(from);
            var end = IstDateRange.ToUtcEndInclusive(to);
            query = query.Where(c => c.CreatedAt >= start && c.CreatedAt <= end);
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt).Take(2000).ToListAsync(ct);
        var userIds = rows.Where(r => r.CreatedByUserId.HasValue).Select(r => r.CreatedByUserId!.Value).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
                .Select(u => new { u.UserId, u.FullName }).ToDictionaryAsync(u => u.UserId, u => u.FullName ?? string.Empty, ct);

        return rows.Select(c => new CreditNoteDto(
            c.Id, c.Kind, c.Number, c.CreatedAt, c.InvoiceId, c.InvoiceNumber, c.InvoiceDate, c.PatientName,
            c.Amount, c.TaxableValue, c.TaxAmount, c.RefundedAmount, c.RefundModes,
            c.BillTotalBefore, c.BillTotalAfter, c.Reason,
            c.CreatedByUserId.HasValue && names.TryGetValue(c.CreatedByUserId.Value, out var n) ? n : null)).ToList();
    }
}
