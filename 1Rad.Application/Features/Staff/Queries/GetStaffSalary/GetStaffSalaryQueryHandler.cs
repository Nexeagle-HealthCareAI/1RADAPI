using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Queries.GetStaffSalary;

public class GetStaffSalaryQueryHandler : IRequestHandler<GetStaffSalaryQuery, StaffSalaryDto?>
{
    private readonly IApplicationDbContext _context;

    public GetStaffSalaryQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<StaffSalaryDto?> Handle(GetStaffSalaryQuery request, CancellationToken cancellationToken)
    {
        var staff = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (staff == null) return null;

        var revisions = await _context.SalaryRevisions
            .Where(r => r.StaffId == request.StaffId)
            .OrderBy(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken);

        var disbursements = await _context.SalaryDisbursements
            .Where(d => d.StaffId == request.StaffId)
            .OrderBy(d => d.Month)
            .ToListAsync(cancellationToken);

        return new StaffSalaryDto(
            staff.StaffId,
            revisions.Select(r => new SalaryRevisionDto(
                r.RevisionId,
                r.EffectiveFrom.ToString("yyyy-MM-dd"),
                r.BasicPay, r.Hra, r.Travel, r.OtherAllowances,
                r.PfDeduction, r.Tds, r.OtherDeductions,
                r.Note, r.CreatedAt
            )).ToList(),
            disbursements.Select(d => new SalaryDisbursementDto(
                d.DisbursementId, d.RevisionId, d.Month, d.Status,
                d.GrossPay, d.NetPay, d.StructureGross, d.StructureNet,
                d.LwpDays, d.LwpDeduction, d.PerDayRate,
                d.PaidLeaveInMonth, d.LwpLeaveInMonth, d.AttendanceJson,
                d.EncashmentDays, d.EncashmentBonus, d.ExtraPay, d.ExtraPayReason,
                d.PaymentMode, d.Reference,
                d.PaidOnDate.ToString("yyyy-MM-dd"),
                d.Notes, d.CreatedAt
            )).ToList()
        );
    }
}
