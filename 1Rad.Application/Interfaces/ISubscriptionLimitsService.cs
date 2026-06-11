using System;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Interfaces
{
    /// <summary>
    /// Resolves a hospital's tier seat/site caps and current usage so the
    /// staff-add and center-add flows can block at the limit.
    /// </summary>
    public interface ISubscriptionLimitsService
    {
        /// <summary>User (staff) cap + current active mapping count for the hospital.</summary>
        Task<LimitStatus> GetUserLimitAsync(Guid hospitalId, CancellationToken cancellationToken = default);

        /// <summary>Site (center) cap + current count of centers in the hospital's group.</summary>
        Task<LimitStatus> GetSiteLimitAsync(Guid hospitalId, CancellationToken cancellationToken = default);
    }

    /// <summary>A cap (null = unlimited) and the current count against it.</summary>
    public sealed record LimitStatus(int? Max, int Current)
    {
        public bool AtLimit => Max.HasValue && Current >= Max.Value;
    }
}
