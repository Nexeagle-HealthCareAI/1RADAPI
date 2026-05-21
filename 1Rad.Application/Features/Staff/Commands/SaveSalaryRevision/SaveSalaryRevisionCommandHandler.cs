using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.SaveSalaryRevision;

public class SaveSalaryRevisionCommandHandler : IRequestHandler<SaveSalaryRevisionCommand, (Guid RevisionId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public SaveSalaryRevisionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(Guid RevisionId, string? Error)> Handle(SaveSalaryRevisionCommand request, CancellationToken cancellationToken)
    {
        var staff = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (staff == null) return (Guid.Empty, "Staff not found.");

        if (!DateOnly.TryParse(request.EffectiveFrom, out var effective))
            return (Guid.Empty, "Invalid effective date.");

        // Upsert by (StaffId, EffectiveFrom)
        var existing = await _context.SalaryRevisions
            .FirstOrDefaultAsync(r => r.StaffId == request.StaffId && r.EffectiveFrom == effective, cancellationToken);

        if (existing != null)
        {
            existing.BasicPay        = request.BasicPay;
            existing.Hra             = request.Hra;
            existing.Travel          = request.Travel;
            existing.OtherAllowances = request.OtherAllowances;
            existing.PfDeduction     = request.PfDeduction;
            existing.Tds             = request.Tds;
            existing.OtherDeductions = request.OtherDeductions;
            existing.Note            = request.Note?.Trim();
            await _context.SaveChangesAsync(cancellationToken);
            return (existing.RevisionId, null);
        }

        var rev = new SalaryRevision
        {
            StaffId         = request.StaffId,
            HospitalId      = request.HospitalId,
            EffectiveFrom   = effective,
            BasicPay        = request.BasicPay,
            Hra             = request.Hra,
            Travel          = request.Travel,
            OtherAllowances = request.OtherAllowances,
            PfDeduction     = request.PfDeduction,
            Tds             = request.Tds,
            OtherDeductions = request.OtherDeductions,
            Note            = request.Note?.Trim(),
            CreatedByUserId = request.CreatedByUserId,
        };
        _context.SalaryRevisions.Add(rev);
        await _context.SaveChangesAsync(cancellationToken);
        return (rev.RevisionId, null);
    }
}
