using System;
using System.Linq;
using System.Text.RegularExpressions;
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
        var contact = request.Contact ?? string.Empty;
        var digits = new string(contact.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12)
        {
            digits = digits.Substring(2);
        }
        else if (digits.StartsWith("0") && digits.Length == 11)
        {
            digits = digits.Substring(1);
        }

        if (digits.Length != 10 || !Regex.IsMatch(digits, "^[6-9][0-9]{9}$"))
        {
            throw new ValidationException("Contact", "Please enter a valid 10-digit Indian mobile number.");
        }

        var referrer = new Referrer
        {
            Name = request.Name,
            Contact = digits,
            Address = request.Address,
            HospitalId = _context.UserContext.HospitalId
        };

        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync(cancellationToken);

        return referrer.ReferrerId;
    }
}
