using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.UpsertKeyword;

public record UpsertKeywordCommand : IRequest<ReportingKeyword>
{
    public Guid Id { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public string ReplacementText { get; init; } = string.Empty;
}

public class UpsertKeywordCommandHandler : IRequestHandler<UpsertKeywordCommand, ReportingKeyword>
{
    private readonly IApplicationDbContext _context;

    public UpsertKeywordCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ReportingKeyword> Handle(UpsertKeywordCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Trigger))
        {
            throw new ArgumentException("Trigger is required.", nameof(request.Trigger));
        }

        if (string.IsNullOrWhiteSpace(request.ReplacementText))
        {
            throw new ArgumentException("Replacement text is required.", nameof(request.ReplacementText));
        }

        var existing = await _context.ReportingKeywords
            .FirstOrDefaultAsync(k => k.Id == request.Id, cancellationToken);

        if (existing == null)
        {
            // Create new keyword
            var keyword = new ReportingKeyword
            {
                Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
                Trigger = request.Trigger,
                ReplacementText = request.ReplacementText,
                HospitalId = hospitalId,
                DoctorId = doctorId
            };

            _context.ReportingKeywords.Add(keyword);
            await _context.SaveChangesAsync(cancellationToken);
            
            return keyword;
        }
        else
        {
            // Update existing keyword
            // Verify ownership
            if (existing.DoctorId != doctorId)
            {
                throw new UnauthorizedAccessException("You do not have permission to modify this keyword.");
            }

            existing.Trigger = request.Trigger;
            existing.ReplacementText = request.ReplacementText;

            await _context.SaveChangesAsync(cancellationToken);
            
            return existing;
        }
    }
}
