using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Who may write, take over and sign a report — shared by save, finalize and
/// addendum so the rules can't drift apart.
///
///   • Signing (Preliminary / Final / addendum) is for doctors only: the
///     AdminDoctor or Doctor system role in the CURRENT centre (token "rid").
///     A custom role granting the reporting page never grants signing.
///   • A draft written by someone who is not a doctor (a technician typing for
///     the radiologist) can be taken over by any doctor of the centre — the
///     doctor becomes the author and can edit and sign it.
///   • A doctor's draft stays that doctor's (no silent take-over between doctors).
///   • Wrong passwords on sign-off are counted per user; after five in fifteen
///     minutes sign-off is refused until the window passes, so the signing
///     endpoints can't be used to guess passwords.
/// </summary>
public static class ReportSignOff
{
    public const int AdminDoctorRoleId = 1;
    public const int DoctorRoleId = 3;

    private const int MaxPasswordFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    public static bool IsDoctor(IEnumerable<int>? roleIds) =>
        roleIds != null && roleIds.Any(r => r == AdminDoctorRoleId || r == DoctorRoleId);

    /// <summary>Throws unless the caller holds a doctor role in this centre.</summary>
    public static void EnsureCanSign(IUserContext user)
    {
        if (!IsDoctor(user.RoleIds))
            throw new UnauthorizedAccessException("Only a doctor can sign a report.");
    }

    /// <summary>True when <paramref name="userId"/> is a doctor (AdminDoctor/Doctor) in the centre.</summary>
    public static Task<bool> IsDoctorInCentreAsync(IApplicationDbContext context, Guid userId, Guid hospitalId, CancellationToken ct) =>
        context.UserHospitalMappings
            .Where(m => m.UserId == userId && m.HospitalId == hospitalId)
            .SelectMany(m => m.Roles)
            .AnyAsync(r => r.RoleId == AdminDoctorRoleId || r.RoleId == DoctorRoleId, ct);

    /// <summary>
    /// May the current user write to (and sign) a report currently owned by
    /// <paramref name="ownerId"/>? The owner always may; an unowned report may be
    /// claimed; a doctor may take over a draft whose owner is not a doctor.
    /// </summary>
    public static async Task<bool> MayTakeOverAsync(
        IApplicationDbContext context, Guid? ownerId, Guid userId, IEnumerable<int>? userRoleIds,
        Guid hospitalId, CancellationToken ct)
    {
        if (!ownerId.HasValue || ownerId.Value == Guid.Empty || ownerId.Value == userId) return true;
        if (!IsDoctor(userRoleIds)) return false;
        return !await IsDoctorInCentreAsync(context, ownerId.Value, hospitalId, ct);
    }

    /// <summary>
    /// Re-authenticates a signature with the user's password, with a per-user
    /// lock-out after repeated failures. Throws UnauthorizedAccessException.
    /// </summary>
    public static async Task VerifyPasswordAsync(
        User user, string? password, IPasswordHasher hasher, IDistributedRateLimiter? limiter,
        string failureMessage, CancellationToken ct)
    {
        var key = $"sign-fail:{user.UserId}";
        if (limiter != null && await limiter.PeekAsync(key, FailureWindow, ct) >= MaxPasswordFailures)
        {
            throw new UnauthorizedAccessException(
                "Too many wrong passwords. Sign-off is paused for up to 15 minutes — please wait and try again.");
        }

        if (string.IsNullOrEmpty(user.PasswordHash) || !hasher.Verify(password ?? string.Empty, user.PasswordHash))
        {
            if (limiter != null) await limiter.TryAcquireAsync(key, MaxPasswordFailures, FailureWindow, ct);
            throw new UnauthorizedAccessException(failureMessage);
        }
    }
}
