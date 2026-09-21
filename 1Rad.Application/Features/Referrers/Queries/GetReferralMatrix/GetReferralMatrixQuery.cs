using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;

public record MatrixRowDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    int Total,
    Dictionary<string, int> Counts
);

public record ReferralMatrixDto(
    List<string> Cols,
    List<MatrixRowDto> Rows
);

public record GetReferralMatrixQuery(
    string Period,
    DateTime ReferenceDate,
    int WeekIndex,
    string? SearchQuery = null
) : IRequest<ReferralMatrixDto>;

public class GetReferralMatrixQueryHandler : IRequestHandler<GetReferralMatrixQuery, ReferralMatrixDto>
{
    private readonly IApplicationDbContext _context;

    public GetReferralMatrixQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ReferralMatrixDto> Handle(GetReferralMatrixQuery request, CancellationToken cancellationToken)
    {
        var cols = new List<string>();
        DateTime startDate;
        DateTime endDate;
        Func<DateTime, string?> getColKey;

        int year = request.ReferenceDate.Year;
        int month = request.ReferenceDate.Month;

        if (request.Period.ToUpper() == "DAY")
        {
            startDate = request.ReferenceDate.Date;
            endDate = startDate.AddDays(1).AddTicks(-1);
            cols = new List<string> { "Morning (12am-12pm)", "Afternoon (12pm-5pm)", "Evening (5pm-12am)" };
            getColKey = (d) =>
            {
                if (d.Date != startDate.Date) return null;
                int h = d.Hour;
                if (h < 12) return cols[0];
                if (h < 17) return cols[1];
                return cols[2];
            };
        }
        else if (request.Period.ToUpper() == "WEEK")
        {
            int startDay = (request.WeekIndex - 1) * 7 + 1;
            int daysInMonth = DateTime.DaysInMonth(year, month);
            int endDay = request.WeekIndex == 4 ? daysInMonth : startDay + 6;
            
            startDate = new DateTime(year, month, startDay);
            endDate = new DateTime(year, month, endDay, 23, 59, 59);

            for (int i = startDay; i <= endDay; i++)
            {
                var d = new DateTime(year, month, i);
                cols.Add(d.ToString("ddd, MMM d"));
            }

            getColKey = (d) =>
            {
                if (d >= startDate && d <= endDate)
                {
                    return d.ToString("ddd, MMM d");
                }
                return null;
            };
        }
        else if (request.Period.ToUpper() == "MONTH")
        {
            startDate = new DateTime(year, month, 1);
            endDate = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59);
            cols = new List<string> { "Week 1 (1st-7th)", "Week 2 (8th-14th)", "Week 3 (15th-21st)", "Week 4 (22nd-End)" };
            
            getColKey = (d) =>
            {
                if (d.Year == year && d.Month == month)
                {
                    if (d.Day <= 7) return cols[0];
                    if (d.Day <= 14) return cols[1];
                    if (d.Day <= 21) return cols[2];
                    return cols[3];
                }
                return null;
            };
        }
        else // YEAR
        {
            startDate = new DateTime(year, 1, 1);
            endDate = new DateTime(year, 12, 31, 23, 59, 59);
            
            for (int i = 1; i <= 12; i++)
            {
                cols.Add(new DateTime(year, i, 1).ToString("MMM"));
            }

            getColKey = (d) =>
            {
                if (d.Year == year)
                {
                    return d.ToString("MMM");
                }
                return null;
            };
        }

        // The window above is in IST calendar terms; stored timestamps are UTC. Compare
        // against the UTC instants of those IST boundaries, and bucket each visit by its
        // IST wall-clock time below — using the UTC hour/day put an evening visit in the
        // wrong slot (Morning/Afternoon/Evening) or on the next day's column.
        var startUtc = IstDateRange.ToUtcStart(startDate);
        var endUtc = IstDateRange.ToUtcEndInclusive(endDate);
        var query = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Patient)
            .ThenInclude(p => p.Referrer)
            .Where(a => a.Patient.ReferrerId != null)
            // Cancelled visits don't count toward referral volume.
            .Where(a => a.Status != "CANCELLED")
            .Where(a => a.DateTime >= startUtc && a.DateTime <= endUtc);

        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            var searchLow = request.SearchQuery.ToLower();
            query = query.Where(a => a.Patient.Referrer.Name.ToLower().Contains(searchLow));
        }

        var appointments = await query
            .Select(a => new
            {
                ReferrerId = a.Patient.ReferrerId,
                ReferrerName = a.Patient.Referrer.Name,
                ReferrerContact = a.Patient.Referrer.Contact,
                a.DateTime
            })
            .ToListAsync(cancellationToken);

        // Duplicates merged into a primary partner roll up under it (the Referrals
        // screen does the same); Self / walk-in is not a partner and is left out.
        var registry = await _context.Referrers.AsNoTracking()
            .Select(r => new { r.ReferrerId, r.Name, r.Contact, r.MergedIntoId })
            .ToListAsync(cancellationToken);
        var refById = registry.ToDictionary(r => r.ReferrerId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (refById.TryGetValue(id, out var n) && n.MergedIntoId.HasValue && seen.Add(id)) id = n.MergedIntoId.Value;
            return id;
        }

        var rows = appointments
            .Where(p => !NameNormalizer.SameName(p.ReferrerName, "Self"))
            .GroupBy(p => Root(p.ReferrerId!.Value))
            .Select(g =>
            {
                var counts = cols.ToDictionary(c => c, c => 0);
                int total = 0;

                foreach (var p in g)
                {
                    var key = getColKey(IstDateRange.ToIst(p.DateTime));
                    if (key != null && counts.ContainsKey(key))
                    {
                        counts[key]++;
                        total++;
                    }
                }

                var primary = refById.TryGetValue(g.Key, out var pr) ? pr : null;
                return new MatrixRowDto(
                    g.Key,
                    primary?.Name ?? g.First().ReferrerName,
                    primary?.Contact ?? g.First().ReferrerContact ?? "N/A",
                    total,
                    counts
                );
            })
            .Where(r => r.Total > 0)
            .OrderByDescending(r => r.Total)
            .ToList();

        return new ReferralMatrixDto(cols, rows);
    }
}
