namespace _1Rad.Application.Interfaces;

/// <summary>
/// Marker for heavy, read-only analytics queries (finance matrix, strategic outlook,
/// referral intelligence…). Their results are cached per centre + parameters and reused
/// until the centre's finance/visit data changes (gap analysis P3-9). See
/// AnalyticsCacheBehavior in 1Rad.Infrastructure.
/// </summary>
public interface ICachedAnalyticsQuery { }
