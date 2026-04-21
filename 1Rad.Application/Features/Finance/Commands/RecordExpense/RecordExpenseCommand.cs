using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.RecordExpense;

public record RecordExpenseCommand : IRequest<Guid>
{
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public decimal TaxAmount { get; init; }
    public DateTime? TransactionDate { get; init; }
    public string? PaymentMode { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? VendorName { get; init; }
    public string? CostCenter { get; init; }
    public string? Status { get; init; }
}

public class RecordExpenseCommandHandler : IRequestHandler<RecordExpenseCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public RecordExpenseCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(RecordExpenseCommand request, CancellationToken cancellationToken)
    {
        var expense = new Expense
        {
            Description = request.Description,
            Category = request.Category,
            Amount = request.Amount,
            TaxAmount = request.TaxAmount,
            PaymentMode = request.PaymentMode,
            ReferenceNumber = request.ReferenceNumber,
            VendorName = request.VendorName,
            CostCenter = request.CostCenter,
            Status = request.Status ?? "Paid",
            TransactionDate = request.TransactionDate ?? DateTime.UtcNow,
            HospitalId = _context.UserContext.HospitalId,
            CreatedAt = DateTime.UtcNow
        };

        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync(cancellationToken);

        return expense.Id;
    }
}
