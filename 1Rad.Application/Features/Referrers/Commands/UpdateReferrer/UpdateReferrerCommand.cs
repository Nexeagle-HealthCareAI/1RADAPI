using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;

public record UpdateReferrerCommand(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    string? Email = null,
    string? Specialty = null,
    string? Degree = null
) : IRequest<bool>;

public class UpdateReferrerCommandHandler : IRequestHandler<UpdateReferrerCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferrerCommand request, CancellationToken cancellationToken)
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

        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId, cancellationToken);

        if (referrer == null) return false;

        referrer.Name = request.Name;
        referrer.Contact = digits;
        referrer.Address = request.Address;
        referrer.Email     = string.IsNullOrWhiteSpace(request.Email)     ? null : request.Email.Trim();
        referrer.Specialty = string.IsNullOrWhiteSpace(request.Specialty) ? null : request.Specialty.Trim();
        referrer.Degree    = string.IsNullOrWhiteSpace(request.Degree)    ? null : request.Degree.Trim();

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
