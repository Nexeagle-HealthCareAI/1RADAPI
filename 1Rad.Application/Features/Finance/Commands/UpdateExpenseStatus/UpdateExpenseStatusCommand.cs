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
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.Id == request.Id, cancellationToken);

        if (expense == null)
            return false;

        expense.Status = request.Status;
        await _context.SaveChangesAsync(cancellationToken);
        
        return true;
    }
}
