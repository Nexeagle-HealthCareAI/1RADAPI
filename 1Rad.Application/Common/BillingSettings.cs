using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace _1Rad.Application.Common;

/// <summary>
/// A centre's tax and billing switches (<see cref="HospitalBillingSettings"/>) with
/// the server defaults filled in. Everything that charges GST, issues credit notes,
/// deducts TDS or limits discounts reads it from here, so the admin's choice applies
/// on every path.
/// </summary>
public static class BillingSettings
{
    public sealed record Effective(
        bool GstEnabled,
        bool CreditNotesEnabled,
        bool TdsEnabled,
        string DefaultTdsCategory,
        decimal TdsCommissionRate,
        decimal TdsCommissionThreshold,
        decimal TdsProfessionalRate,
        decimal TdsProfessionalThreshold,
        decimal TdsNoPanRate,
        decimal FrontDeskDiscountLimitPercent);

    public static Effective Defaults(IConfiguration? config) => Resolve(null, config);

    public static Effective Resolve(HospitalBillingSettings? row, IConfiguration? config)
    {
        var com = ReferralTds.RuleFor(ReferralTds.Commission, config)!;
        var pro = ReferralTds.RuleFor(ReferralTds.Professional, config)!;
        return new Effective(
            row?.GstEnabled ?? true,
            row?.CreditNotesEnabled ?? true,
            row?.TdsEnabled ?? true,
            NormaliseCategory(row?.DefaultTdsCategory) ?? ReferralTds.Commission,
            row?.TdsCommissionRate ?? com.Rate,
            row?.TdsCommissionThreshold ?? com.Threshold,
            row?.TdsProfessionalRate ?? pro.Rate,
            row?.TdsProfessionalThreshold ?? pro.Threshold,
            row?.TdsNoPanRate ?? ReferralTds.NoPanRate(config),
            row?.FrontDeskDiscountLimitPercent ?? DiscountPolicy.FrontDeskLimitPercent(config));
    }

    public static async Task<Effective> GetAsync(IApplicationDbContext db, Guid hospitalId, IConfiguration? config, CancellationToken ct)
    {
        if (hospitalId == Guid.Empty) return Defaults(config);
        var row = await db.HospitalBillingSettings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.HospitalId == hospitalId, ct);
        return Resolve(row, config);
    }

    public static string? NormaliseCategory(string? c)
    {
        var v = (c ?? string.Empty).Trim().ToUpperInvariant();
        return v is ReferralTds.Commission or ReferralTds.Professional or ReferralTds.None ? v : null;
    }

    /// <summary>
    /// The server settings with this centre's rates laid over them, for code that reads
    /// ReferralTds:* and Billing:FrontDeskDiscountLimitPercent.
    /// </summary>
    public static IConfiguration Overlay(Effective s, IConfiguration? baseConfig)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new OverlayConfiguration(baseConfig, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ReferralTds:Commission:Rate"] = s.TdsCommissionRate.ToString(inv),
            ["ReferralTds:Commission:Threshold"] = s.TdsCommissionThreshold.ToString(inv),
            ["ReferralTds:Professional:Rate"] = s.TdsProfessionalRate.ToString(inv),
            ["ReferralTds:Professional:Threshold"] = s.TdsProfessionalThreshold.ToString(inv),
            ["ReferralTds:NoPanRate"] = s.TdsNoPanRate.ToString(inv),
            ["Billing:FrontDeskDiscountLimitPercent"] = s.FrontDeskDiscountLimitPercent.ToString(inv),
        });
    }

    private sealed class OverlayConfiguration : IConfiguration
    {
        private readonly IConfiguration? _base;
        private readonly Dictionary<string, string?> _over;
        public OverlayConfiguration(IConfiguration? b, Dictionary<string, string?> over) { _base = b; _over = over; }
        public string? this[string key]
        {
            get => _over.TryGetValue(key, out var v) ? v : _base?[key];
            set => _over[key] = value;
        }
        public IConfigurationSection GetSection(string key) =>
            _base?.GetSection(key) ?? throw new InvalidOperationException("No base configuration.");
        public IEnumerable<IConfigurationSection> GetChildren() => _base?.GetChildren() ?? Enumerable.Empty<IConfigurationSection>();
        public IChangeToken GetReloadToken() => _base?.GetReloadToken() ?? new CancellationChangeToken(CancellationToken.None);
    }
}
