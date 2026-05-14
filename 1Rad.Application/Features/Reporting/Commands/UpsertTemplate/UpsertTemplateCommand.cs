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
                HospitalId = hospitalId
            };

            _context.ReportTemplates.Add(template);
            await _context.SaveChangesAsync(cancellationToken);
            
            // SYNC WITH SERVICE CHARGE
            await SynchronizeServiceCharge(template, cancellationToken);

            return template;
        }
        else
        {
            // Update existing template
            // Verify ownership
            if (existing.HospitalId != hospitalId)
            {
                throw new UnauthorizedAccessException("You do not have permission to modify this template.");
            }

            existing.Name = request.Name;
            existing.Modality = request.Modality;
            existing.Content = request.Content;

            await _context.SaveChangesAsync(cancellationToken);

            // SYNC WITH SERVICE CHARGE
            await SynchronizeServiceCharge(existing, cancellationToken);
            
            return existing;
        }
    }

    private async Task SynchronizeServiceCharge(ReportTemplate template, CancellationToken cancellationToken)
    {
        // 1. Check if a ServiceCharge is already linked to this template
        var linkedService = await _context.ServiceCharges
            .FirstOrDefaultAsync(s => s.TemplateId == template.Id, cancellationToken);

        if (linkedService == null)
        {
            // 2. If not linked, check if a ServiceCharge with the same name exists in this hospital/modality
            linkedService = await _context.ServiceCharges
                .FirstOrDefaultAsync(s => s.HospitalId == template.HospitalId && 
                                         s.Modality == template.Modality && 
                                         s.ServiceName == template.Name, cancellationToken);
            
            if (linkedService != null)
            {
                // Link existing service to this template
                linkedService.TemplateId = template.Id;
            }
            else
            {
                // 3. Create new ServiceCharge if none exists
                linkedService = new ServiceCharge
                {
                    HospitalId = template.HospitalId,
                    Modality = template.Modality,
                    ServiceName = template.Name,
                    Amount = 0, // Default to 0, to be configured in billing
                    TemplateId = template.Id
                };
                _context.ServiceCharges.Add(linkedService);
            }
        }
        else
        {
            // 4. Update existing linked service name if it changed
            linkedService.ServiceName = template.Name;
            linkedService.Modality = template.Modality;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
