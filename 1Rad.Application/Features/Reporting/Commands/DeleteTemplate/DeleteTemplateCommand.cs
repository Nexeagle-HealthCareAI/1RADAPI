using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.DeleteTemplate;

public record DeleteTemplateCommand : IRequest<bool>
{
    public Guid Id { get; init; }
}

public class DeleteTemplateCommandHandler : IRequestHandler<DeleteTemplateCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeleteTemplateCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(DeleteTemplateCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        var template = await _context.ReportTemplates
            .FirstOrDefaultAsync(t => 
                t.Id == request.Id && 
                t.HospitalId == hospitalId && 
                (t.DoctorId == null || t.DoctorId == doctorId), 
                cancellationToken);

        if (template == null)
        {
            throw new KeyNotFoundException("Template not found or you do not have permission to delete it.");
        }

        _context.ReportTemplates.Remove(template);
        await _context.SaveChangesAsync(cancellationToken);
        
        return true;
    }
}
