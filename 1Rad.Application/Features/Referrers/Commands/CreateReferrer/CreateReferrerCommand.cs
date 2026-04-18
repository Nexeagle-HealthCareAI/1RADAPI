using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;

namespace _1Rad.Application.Features.Referrers.Commands.CreateReferrer;

public record CreateReferrerCommand(string Name, string Contact, string Address) : IRequest<Guid>;

public class CreateReferrerCommandHandler : IRequestHandler<CreateReferrerCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreateReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(CreateReferrerCommand request, CancellationToken cancellationToken)
    {
        var referrer = new Referrer
        {
            Name = request.Name,
            Contact = request.Contact,
            Address = request.Address,
            HospitalId = _context.UserContext.HospitalId
        };

        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync(cancellationToken);

        return referrer.ReferrerId;
    }
}
