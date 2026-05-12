using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;

public record UpdateReferrerCommand(Guid ReferrerId, string Name, string Contact, string Address) : IRequest<bool>;

public class UpdateReferrerCommandHandler : IRequestHandler<UpdateReferrerCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferrerCommand request, CancellationToken cancellationToken)
    {
        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId, cancellationToken);

        if (referrer == null) return false;

        referrer.Name = request.Name;
        referrer.Contact = request.Contact;
        referrer.Address = request.Address;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
