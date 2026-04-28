using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.DeleteKeyword;

public record DeleteKeywordCommand : IRequest<bool>
{
    public Guid Id { get; init; }
}

public class DeleteKeywordCommandHandler : IRequestHandler<DeleteKeywordCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeleteKeywordCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(DeleteKeywordCommand request, CancellationToken cancellationToken)
    {
        var doctorId = _context.UserContext.UserId;

        var keyword = await _context.ReportingKeywords
            .FirstOrDefaultAsync(k => k.Id == request.Id && k.DoctorId == doctorId, cancellationToken);

        if (keyword == null)
        {
            throw new KeyNotFoundException("Keyword not found or you do not have permission to delete it.");
        }

        _context.ReportingKeywords.Remove(keyword);
        await _context.SaveChangesAsync(cancellationToken);
        
        return true;
    }
}
