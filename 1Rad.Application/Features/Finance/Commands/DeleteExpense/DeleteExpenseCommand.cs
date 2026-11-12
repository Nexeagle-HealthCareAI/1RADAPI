using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Commands.DeleteExpense;

public record DeleteExpenseCommand(Guid ExpenseId) : IRequest<bool>;

public class DeleteExpenseCommandHandler : IRequestHandler<DeleteExpenseCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeleteExpenseCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(DeleteExpenseCommand request, CancellationToken cancellationToken)
    {
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == request.ExpenseId && e.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (expense == null)
        {
            throw new Exception("Expense not found or unauthorized.");
        }

        if (expense.LinkedDisbursementId != null)
            throw new _1Rad.Domain.Exceptions.BusinessRuleViolationException("This expense comes from Staff & salary. Change it there.");
        // Soft delete (review B28): offline copies see the tombstone, and the history stays.
        expense.DeletedAt = DateTime.UtcNow;
        expense.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }
}
