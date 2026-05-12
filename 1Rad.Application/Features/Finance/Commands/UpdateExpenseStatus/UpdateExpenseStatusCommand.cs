using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.UpdateExpenseStatus;

public record UpdateExpenseStatusCommand(Guid Id, string Status) : IRequest<bool>;

public class UpdateExpenseStatusCommandHandler : IRequestHandler<UpdateExpenseStatusCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateExpenseStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateExpenseStatusCommand request, CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required for financial state transitions.");

        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.HospitalId == _context.UserContext.HospitalId, cancellationToken);

        if (expense == null)
            throw new Exception($"FISCAL ERROR: Expense record [{request.Id}] not found in center ledger.");

        expense.Status = request.Status;
        await _context.SaveChangesAsync(cancellationToken);
        
        return true;
    }
}

