using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.PayExpense;

/// <summary>
/// Money ▸ Expenses "Mark paid": the expense was paid now, from the cash drawer, by
/// UPI or from the bank. The cash book counts it today, under that mode.
/// </summary>
public record PayExpenseCommand(Guid Id, string Mode, string? Reference) : IRequest<bool>;

public class PayExpenseCommandHandler : IRequestHandler<PayExpenseCommand, bool>
{
    private readonly IApplicationDbContext _db;
    public PayExpenseCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<bool> Handle(PayExpenseCommand c, CancellationToken ct)
    {
        var e = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == c.Id && x.HospitalId == _db.UserContext.HospitalId && x.DeletedAt == null, ct)
                ?? throw new NotFoundException("Expense not found.");
        if (e.LinkedDisbursementId != null)
            throw new BusinessRuleViolationException("This expense comes from Staff & salary. Pay it there.");
        if (e.Status == "Paid") throw new BusinessRuleViolationException("This expense is already paid.");
        var mode = (c.Mode ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "CASH" => "Cash",
            "UPI" => "UPI",
            "CARD" => "Card",
            "BANK" or "BANK TRANSFER" or "NEFT" or "IMPS" or "RTGS" => "Bank Transfer",
            "CHEQUE" => "Cheque",
            _ => throw new ValidationException("Paid from the cash drawer, UPI, card, bank or cheque."),
        };
        e.Status = "Paid";
        e.PaymentMode = mode;
        if (!string.IsNullOrWhiteSpace(c.Reference)) e.ReferenceNumber = c.Reference.Trim().Length > 100 ? c.Reference.Trim()[..100] : c.Reference.Trim();
        e.PaidAt = DateTime.UtcNow;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
