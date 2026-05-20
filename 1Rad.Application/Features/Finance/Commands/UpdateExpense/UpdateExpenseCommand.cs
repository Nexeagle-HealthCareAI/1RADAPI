using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.UpdateExpense;

public record UpdateExpenseCommand : IRequest<bool>
{
    public Guid Id { get; init; }
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

public class UpdateExpenseCommandHandler : IRequestHandler<UpdateExpenseCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateExpenseCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateExpenseCommand request, CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required to update expenses.");

        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (expense == null)
            throw new KeyNotFoundException($"Expense [{request.Id}] not found in centre ledger.");

        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("Description is required.", nameof(request.Description));

        if (string.IsNullOrWhiteSpace(request.Category))
            throw new ArgumentException("Category is required.", nameof(request.Category));

        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.", nameof(request.Amount));

        expense.Description = request.Description;
        expense.Category = request.Category;
        expense.Amount = request.Amount;
        expense.TaxAmount = request.TaxAmount;
        expense.PaymentMode = request.PaymentMode;
        expense.ReferenceNumber = request.ReferenceNumber;
        expense.VendorName = request.VendorName;
        expense.CostCenter = request.CostCenter;
        expense.Status = request.Status ?? expense.Status;
        expense.TransactionDate = request.TransactionDate ?? expense.TransactionDate;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
