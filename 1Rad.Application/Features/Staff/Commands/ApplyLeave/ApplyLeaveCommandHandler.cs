using _1Rad.Application.Common;
using _1Rad.Application.Features.Staff.LeaveBalances;
using _1Rad.Application.Features.Staff.WorkDays;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.ApplyLeave;

public class ApplyLeaveCommandHandler
    : IRequestHandler<ApplyLeaveCommand, (Guid LeaveRequestId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public ApplyLeaveCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<(Guid LeaveRequestId, string? Error)> Handle(
        ApplyLeaveCommand request, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParse(request.FromDate, out var from))
            return (Guid.Empty, "Invalid fromDate — expected YYYY-MM-DD.");
        if (!DateOnly.TryParse(request.ToDate, out var to))
            return (Guid.Empty, "Invalid toDate — expected YYYY-MM-DD.");
        if (to < from)
            return (Guid.Empty, "toDate cannot be before fromDate.");
        if (string.IsNullOrWhiteSpace(request.LeaveType))
            return (Guid.Empty, "LeaveType is required.");

        var staff = await _context.StaffMembers.AsNoTracking()
            .Where(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId)
            .Select(s => new { s.WeeklyOff }).FirstOrDefaultAsync(cancellationToken);
        if (staff == null)
            return (Guid.Empty, "Staff not found.");
        if (to.DayNumber - from.DayNumber > 366)
            return (Guid.Empty, "Leave can be at most a year long.");

        // Leave is counted on this person's working days only (Nov 2026): weekly
        // offs and the centre's holidays inside the range do not use up leave.
        var cal = await WorkCalendar.LoadAsync(_context, request.HospitalId, from, to, cancellationToken);
        var personal = WorkCalendar.Split(staff.WeeklyOff);
        var days = cal.WorkingDays(from, to, personal);
        if (days == 0)
            return (Guid.Empty, "Every day in these dates is a weekly off or a holiday — no leave is needed.");

        // Paid leave cannot go past what is left (approved and waiting requests both count).
        var rules = await LeavePolicyRules.LoadAsync(_context, request.HospitalId, cancellationToken);
        var leaveType = request.LeaveType.Trim();
        if (rules.Count > 0)
        {
            var rule = rules.FirstOrDefault(r => string.Equals(r.Name, leaveType, StringComparison.OrdinalIgnoreCase));
            // The Staff page always offers "Unpaid Leave (LWP)" even when the policy has no unpaid type.
            var unpaidLabel = leaveType.Contains("LWP", StringComparison.OrdinalIgnoreCase)
                              || leaveType.StartsWith("Unpaid", StringComparison.OrdinalIgnoreCase);
            if (rule == null && !unpaidLabel)
                return (Guid.Empty, $"{leaveType} is not in the leave policy.");
            if (rule != null) leaveType = rule.Name;
            if (rule is { IsPaid: true })
            {
                var today = DateOnly.FromDateTime(IstDateRange.ToIst(DateTime.UtcNow));
                for (var y = from.Year; y <= to.Year; y++)
                {
                    var a = from.Year == y ? from : new DateOnly(y, 1, 1);
                    var b = to.Year == y ? to : new DateOnly(y, 12, 31);
                    var need = cal.WorkingDays(a, b, personal);
                    if (need == 0) continue;
                    var bal = (await LeaveBalanceCalculator.ComputeAsync(_context, request.HospitalId, y,
                        new[] { request.StaffId }, new List<LeaveTypeRule> { rule }, today, cancellationToken))
                        .First().Types.First();
                    var left = bal.Balance - bal.Pending;
                    if (need > left)
                        return (Guid.Empty, left <= 0
                            ? $"No {rule.Name} left in {y}."
                            : $"Only {left:0.#} day(s) of {rule.Name} left in {y}; these dates need {need}.");
                }
            }
        }

        var entry = new StaffLeaveRequest
        {
            StaffId    = request.StaffId,
            HospitalId = request.HospitalId,
            LeaveType  = leaveType,
            FromDate   = from,
            ToDate     = to,
            Days       = days,
            Reason     = request.Reason?.Trim(),
        };
        _context.StaffLeaveRequests.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return (entry.LeaveRequestId, null);
    }
}
