using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Common;

/// <summary>
/// Generates per-hospital sequential employee codes in the format "EMP-NNNN".
/// Also backfills codes for any legacy staff records that don't have one yet.
/// </summary>
public static class EmployeeCodeGenerator
{
    private const string Prefix = "EMP-";
    private const int PadWidth = 4;

    /// <summary>
    /// Returns the next available "EMP-NNNN" code for the given hospital.
    /// Walks existing codes to find the highest sequence number, then increments.
    /// </summary>
    public static async Task<string> NextCodeAsync(IApplicationDbContext context, Guid hospitalId, CancellationToken ct)
    {
        var existingCodes = await context.StaffMembers
            .Where(s => s.HospitalId == hospitalId && s.EmployeeCode != null)
            .Select(s => s.EmployeeCode!)
            .ToListAsync(ct);

        int highest = 0;
        foreach (var code in existingCodes)
        {
            if (code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(code.Substring(Prefix.Length), out var n))
            {
                if (n > highest) highest = n;
            }
        }
        return $"{Prefix}{(highest + 1).ToString().PadLeft(PadWidth, '0')}";
    }

    /// <summary>
    /// Assigns "EMP-NNNN" codes to any staff in this hospital that don't have one yet.
    /// Ordered by CreatedAt so older records get lower numbers. Persists in a single SaveChanges.
    /// Returns the staff list with codes populated.
    /// </summary>
    public static async Task<List<StaffMember>> BackfillMissingCodesAsync(
        IApplicationDbContext context, Guid hospitalId, CancellationToken ct)
    {
        var all = await context.StaffMembers
            .Where(s => s.HospitalId == hospitalId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        int highest = 0;
        foreach (var s in all.Where(s => !string.IsNullOrEmpty(s.EmployeeCode)))
        {
            if (s.EmployeeCode!.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(s.EmployeeCode.Substring(Prefix.Length), out var n)
                && n > highest)
            {
                highest = n;
            }
        }

        var modified = false;
        foreach (var s in all.Where(s => string.IsNullOrEmpty(s.EmployeeCode)))
        {
            highest += 1;
            s.EmployeeCode = $"{Prefix}{highest.ToString().PadLeft(PadWidth, '0')}";
            modified = true;
        }
        if (modified) await context.SaveChangesAsync(ct);
        return all;
    }
}
