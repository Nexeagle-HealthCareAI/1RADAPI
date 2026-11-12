using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoiceHistory;

/// <summary>
/// Money ▸ Bills: one bill and everything that happened to its money, oldest first —
/// made, each payment (with receipt number, mode, reference and who took it), money
/// handed back, advances held, credit notes and refund vouchers, and approval
/// requests. Nothing here is edited later, so the list is the bill's full story.
/// </summary>
public record GetInvoiceHistoryQuery(Guid InvoiceId) : IRequest<InvoiceHistoryDto>;

public record InvoiceHistoryLine(string Description, decimal Amount, bool IsFree);

public record InvoiceHistoryEvent(
    DateTime At, string Kind, decimal Amount, string? Mode, string? Reference,
    string? Document, string? By, string? Note);

public record InvoiceHistoryDto(
    Guid Id, string Number, string Status, DateTime CreatedAt, DateTime ServiceDate,
    Guid PatientId, string? PatientName, string? Partner,
    List<InvoiceHistoryLine> Lines,
    decimal Gross, decimal CentreDiscount, decimal ReferrerDiscount, decimal Deduction, decimal ExtraCharges,
    decimal Total, decimal Paid, decimal Balance, string? DiscountReason, string? DiscountBy,
    bool HasMoney, string? RowVersion,
    List<InvoiceHistoryEvent> Events);

public static class InvoiceHistoryKinds
{
    public const string Made = "BILL_MADE";
    public const string Payment = "PAYMENT";
    public const string Refund = "REFUND";
    public const string AdvanceHeld = "ADVANCE_HELD";
    public const string CreditNote = "CREDIT_NOTE";
    public const string RefundVoucher = "REFUND_VOUCHER";
    public const string ApprovalAsked = "APPROVAL_ASKED";
    public const string ApprovalDone = "APPROVAL_DONE";
    public const string Cancelled = "CANCELLED";
}

public class GetInvoiceHistoryQueryHandler : IRequestHandler<GetInvoiceHistoryQuery, InvoiceHistoryDto>
{
    private readonly IApplicationDbContext _db;
    public GetInvoiceHistoryQueryHandler(IApplicationDbContext db) => _db = db;

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public async Task<InvoiceHistoryDto> Handle(GetInvoiceHistoryQuery q, CancellationToken ct)
    {
        var hospitalId = _db.UserContext.HospitalId;
        var inv = await _db.Invoices.AsNoTracking()
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == q.InvoiceId && i.HospitalId == hospitalId, ct)
            ?? throw new NotFoundException("Bill not found.");

        string? partner = null;
        if (inv.AppointmentId is Guid apptId)
        {
            partner = await _db.Appointments.AsNoTracking()
                .Where(a => a.AppointmentId == apptId && a.HospitalId == hospitalId)
                .Select(a => a.ReferredBy)
                .FirstOrDefaultAsync(ct);
        }

        var notes = await _db.CreditNotes.AsNoTracking()
            .Where(n => n.InvoiceId == inv.Id && n.HospitalId == hospitalId)
            .ToListAsync(ct);
        var advances = await _db.CreditTransactions.AsNoTracking()
            .Where(c => c.InvoiceId == inv.Id && c.HospitalId == hospitalId && c.DeletedAt == null && c.Type == "ADVANCE")
            .ToListAsync(ct);
        var approvals = await _db.ApprovalRequests.AsNoTracking()
            .Where(a => a.InvoiceId == inv.Id && a.HospitalId == hospitalId && a.DeletedAt == null)
            .ToListAsync(ct);

        var userIds = inv.Payments.Select(p => p.CreatedByUserId)
            .Concat(notes.Select(n => n.CreatedByUserId))
            .Concat(advances.Select(a => a.CreatedByUserId))
            .Concat(approvals.Select(a => (Guid?)a.RequestedBy))
            .Concat(approvals.Select(a => a.ReviewedBy))
            .Append(inv.DiscountByUserId)
            .Where(g => g.HasValue && g.Value != Guid.Empty).Select(g => g!.Value).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
                .ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        string? Name(Guid? id) => id.HasValue && names.TryGetValue(id.Value, out var n) ? n : null;

        var events = new List<InvoiceHistoryEvent>
        {
            new(Utc(inv.CreatedAt), InvoiceHistoryKinds.Made, inv.GrossAmount, null, null, inv.InvoiceId, null,
                inv.Items.Count == 1 ? inv.Items.First().Description : $"{inv.Items.Count} tests"),
        };
        foreach (var p in inv.Payments)
        {
            var isRefund = p.Kind == PaymentKinds.Refund || p.Amount < 0;
            events.Add(new(Utc(p.CreatedAt), isRefund ? InvoiceHistoryKinds.Refund : InvoiceHistoryKinds.Payment,
                p.Amount, p.PaymentMethod, p.Reference, p.ReceiptNumber, Name(p.CreatedByUserId), p.Reason));
        }
        foreach (var a in advances)
            events.Add(new(Utc(a.CreatedAt), InvoiceHistoryKinds.AdvanceHeld, a.Amount, a.PaymentMethod, null, null,
                Name(a.CreatedByUserId), a.Remarks));
        foreach (var n in notes)
            events.Add(new(Utc(n.CreatedAt),
                n.Kind == CreditNoteKinds.RefundVoucher ? InvoiceHistoryKinds.RefundVoucher : InvoiceHistoryKinds.CreditNote,
                n.Amount, n.RefundModes, null, n.Number, Name(n.CreatedByUserId), n.Reason));
        foreach (var a in approvals)
        {
            events.Add(new(Utc(a.CreatedAt), InvoiceHistoryKinds.ApprovalAsked, 0, null, null, a.Type,
                Name(a.RequestedBy), a.Reason));
            if (a.ReviewedAt is DateTime at && !string.Equals(a.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
                events.Add(new(Utc(at), InvoiceHistoryKinds.ApprovalDone, 0, null, null, a.Status,
                    Name(a.ReviewedBy), a.ReviewNote));
        }
        if (string.Equals(inv.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            events.Add(new(Utc(inv.DeletedAt ?? inv.UpdatedAt), InvoiceHistoryKinds.Cancelled, 0, null, null, null, null, null));

        var hasMoney = inv.Payments.Any(p => p.Amount > 0.009m) || inv.PaidAmount > 0.009m;
        return new InvoiceHistoryDto(
            inv.Id, inv.InvoiceId, inv.Status, Utc(inv.CreatedAt), Utc(inv.ServiceDate),
            inv.PatientId, inv.PatientName, partner,
            inv.Items.Select(i => new InvoiceHistoryLine(i.Description, i.Amount, i.IsFree)).ToList(),
            inv.GrossAmount, inv.CentreDiscount, inv.ReferrerDiscount, inv.InstitutionalDeduction, inv.AdditionalCharges,
            inv.TotalAmount, inv.PaidAmount, Math.Round(inv.TotalAmount - inv.PaidAmount, 2),
            inv.DiscountReason, Name(inv.DiscountByUserId),
            hasMoney, inv.RowVersion == null ? null : Convert.ToBase64String(inv.RowVersion),
            events.OrderBy(e => e.At).ToList());
    }
}
