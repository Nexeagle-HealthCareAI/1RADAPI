using System;
using System.Linq;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
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
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException("Name", "Referrer name is required.");
        }

        // Contact is optional and unvalidated. Light-sanitize any digits the
        // caller supplied (strip 91/0 prefixes) so stored numbers stay tidy,
        // but accept blank or non-standard values without rejecting.
        var contact = (request.Contact ?? string.Empty).Trim();
        var digits = new string(contact.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12)
        {
            digits = digits.Substring(2);
        }
        else if (digits.StartsWith("0") && digits.Length == 11)
        {
            digits = digits.Substring(1);
        }
        var storedContact = digits.Length > 0 ? digits : contact;

        var referrer = new Referrer
        {
            Name = request.Name,
            Contact = storedContact,
            Address = request.Address,
            HospitalId = _context.UserContext.HospitalId
        };

        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync(cancellationToken);

        return referrer.ReferrerId;
    }
}
