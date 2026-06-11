using System;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Infrastructure.Services
{
    /// <summary>
    /// Direct-query implementation (staff/center adds are infrequent, so no
    /// cache — and an at-the-gate count avoids racing past the cap). Caps come
    /// from the hospital's latest subscription; usage from live counts.
    /// </summary>
    public class SubscriptionLimitsService : ISubscriptionLimitsService
    {
        private readonly IApplicationDbContext _db;

        public SubscriptionLimitsService(IApplicationDbContext db) => _db = db;

        public async Task<LimitStatus> GetUserLimitAsync(Guid hospitalId, CancellationToken cancellationToken = default)
        {
            var max = await _db.HospitalSubscriptions
                .AsNoTracking()
                .Where(s => s.HospitalId == hospitalId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.MaxUsers)
                .FirstOrDefaultAsync(cancellationToken);

            var current = await _db.UserHospitalMappings
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(m => m.HospitalId == hospitalId, cancellationToken);

            return new LimitStatus(max, current);
        }

        public async Task<LimitStatus> GetSiteLimitAsync(Guid hospitalId, CancellationToken cancellationToken = default)
        {
            var max = await _db.HospitalSubscriptions
                .AsNoTracking()
                .Where(s => s.HospitalId == hospitalId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.MaxSites)
                .FirstOrDefaultAsync(cancellationToken);

            var groupId = await _db.Hospitals
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(h => h.HospitalId == hospitalId)
                .Select(h => h.GroupId)
                .FirstOrDefaultAsync(cancellationToken);

            // Sites = centers sharing this hospital's group; a group-less hospital is 1.
            var current = (groupId != null && groupId != Guid.Empty)
                ? await _db.Hospitals.AsNoTracking().IgnoreQueryFilters().CountAsync(h => h.GroupId == groupId, cancellationToken)
                : 1;

            return new LimitStatus(max, current);
        }
    }
}
