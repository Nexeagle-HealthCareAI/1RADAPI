using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.UpsertTemplate;

public record UpsertTemplateCommand : IRequest<ReportTemplate>
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Modality { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool IsStructured { get; init; }
}

public class UpsertTemplateCommandHandler : IRequestHandler<UpsertTemplateCommand, ReportTemplate>
{
    private readonly IApplicationDbContext _context;

    public UpsertTemplateCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ReportTemplate> Handle(UpsertTemplateCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Template name is required.", nameof(request.Name));
        }

        if (string.IsNullOrWhiteSpace(request.Modality))
        {
            throw new ArgumentException("Modality is required.", nameof(request.Modality));
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Template content is required.", nameof(request.Content));
        }

        var existing = await _context.ReportTemplates
            .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);

        if (existing == null)
        {
            // Create new template
            var template = new ReportTemplate
            {
                Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
                Name = request.Name,
                Modality = request.Modality,
                Content = request.Content,
                IsStructured = request.IsStructured,
                HospitalId = hospitalId,
                DoctorId = doctorId
            };

            _context.ReportTemplates.Add(template);
            await _context.SaveChangesAsync(cancellationToken);
            
            return template;
        }
        else
        {
            // Update existing template
            // Verify ownership
            if (existing.HospitalId != hospitalId || 
                (existing.DoctorId.HasValue && existing.DoctorId != doctorId))
            {
                throw new UnauthorizedAccessException("You do not have permission to modify this template.");
            }

            existing.Name = request.Name;
            existing.Modality = request.Modality;
            existing.Content = request.Content;
            existing.IsStructured = request.IsStructured;

            await _context.SaveChangesAsync(cancellationToken);
            
            return existing;
        }
    }
}
