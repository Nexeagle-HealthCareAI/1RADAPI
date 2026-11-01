using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Application.Features.Hospitals.BillingSettings;

/// <summary>
/// The centre's tax and billing switches: GST on bills, credit notes, TDS on referral
/// payouts (and its rates), and how much the front desk may take off a bill. Anyone
/// at the centre can read them (the billing screens need them); only its admins can
/// change them. "Defaults" is what applies when a value is left empty.
/// </summary>
public record BillingSettingsDto(
    bool GstEnabled, bool CreditNotesEnabled, bool TdsEnabled, string DefaultTdsCategory,
    decimal TdsCommissionRate, decimal TdsCommissionThreshold, decimal TdsProfessionalRate, decimal TdsProfessionalThreshold,
    decimal TdsNoPanRate, decimal FrontDeskDiscountLimitPercent,
    bool Customised, DateTime? UpdatedAt, string? UpdatedBy, BillingSettingsDefaults Defaults);

public record BillingSettingsDefaults(
    decimal TdsCommissionRate, decimal TdsCommissionThreshold, decimal TdsProfessionalRate, decimal TdsProfessionalThreshold,
    decimal TdsNoPanRate, decimal FrontDeskDiscountLimitPercent);

public record GetBillingSettingsQuery(Guid HospitalId) : IRequest<BillingSettingsDto>;

/// <summary>A null rate, threshold or limit goes back to the default.</summary>
public record UpdateBillingSettingsCommand(
    Guid HospitalId, bool GstEnabled, bool CreditNotesEnabled, bool TdsEnabled, string? DefaultTdsCategory,
    decimal? TdsCommissionRate, decimal? TdsCommissionThreshold, decimal? TdsProfessionalRate, decimal? TdsProfessionalThreshold,
    decimal? TdsNoPanRate, decimal? FrontDeskDiscountLimitPercent) : IRequest<BillingSettingsDto>;

public class BillingSettingsHandlers :
    IRequestHandler<GetBillingSettingsQuery, BillingSettingsDto>,
    IRequestHandler<UpdateBillingSettingsCommand, BillingSettingsDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IConfiguration? _config;

    public BillingSettingsHandlers(IApplicationDbContext db, IConfiguration? config = null)
    {
        _db = db;
        _config = config;
    }

    public Task<BillingSettingsDto> Handle(GetBillingSettingsQuery q, CancellationToken ct) => LoadAsync(q.HospitalId, ct);

    public async Task<BillingSettingsDto> Handle(UpdateBillingSettingsCommand c, CancellationToken ct)
    {
        var category = Common.BillingSettings.NormaliseCategory(c.DefaultTdsCategory ?? ReferralTds.Commission)
                       ?? throw new ValidationException("Default TDS category must be COMMISSION, PROFESSIONAL or NONE.");
        Rate(c.TdsCommissionRate, "Commission TDS rate");
        Rate(c.TdsProfessionalRate, "Professional fees TDS rate");
        Rate(c.TdsNoPanRate, "No-PAN TDS rate");
        Amount(c.TdsCommissionThreshold, "Commission yearly limit");
        Amount(c.TdsProfessionalThreshold, "Professional fees yearly limit");
        if (c.FrontDeskDiscountLimitPercent is < 0 or > 100)
            throw new ValidationException("The front-desk discount limit must be between 0% and 100%.");

        var row = await _db.HospitalBillingSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.HospitalId == c.HospitalId, ct);
        if (row == null)
        {
            row = new HospitalBillingSettings { HospitalId = c.HospitalId };
            _db.HospitalBillingSettings.Add(row);
        }
        row.GstEnabled = c.GstEnabled;
        row.CreditNotesEnabled = c.CreditNotesEnabled;
        row.TdsEnabled = c.TdsEnabled;
        row.DefaultTdsCategory = category;
        row.TdsCommissionRate = c.TdsCommissionRate;
        row.TdsCommissionThreshold = c.TdsCommissionThreshold;
        row.TdsProfessionalRate = c.TdsProfessionalRate;
        row.TdsProfessionalThreshold = c.TdsProfessionalThreshold;
        row.TdsNoPanRate = c.TdsNoPanRate;
        row.FrontDeskDiscountLimitPercent = c.FrontDeskDiscountLimitPercent;
        row.UpdatedByUserId = _db.UserContext.UserId == Guid.Empty ? null : _db.UserContext.UserId;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await LoadAsync(c.HospitalId, ct);
    }

    private async Task<BillingSettingsDto> LoadAsync(Guid hospitalId, CancellationToken ct)
    {
        var row = await _db.HospitalBillingSettings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.HospitalId == hospitalId, ct);
        var e = Common.BillingSettings.Resolve(row, _config);
        var d = Common.BillingSettings.Defaults(_config);
        string? by = null;
        if (row?.UpdatedByUserId is Guid uid)
            by = await _db.Users.AsNoTracking().Where(u => u.UserId == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        return new BillingSettingsDto(
            e.GstEnabled, e.CreditNotesEnabled, e.TdsEnabled, e.DefaultTdsCategory,
            e.TdsCommissionRate, e.TdsCommissionThreshold, e.TdsProfessionalRate, e.TdsProfessionalThreshold,
            e.TdsNoPanRate, e.FrontDeskDiscountLimitPercent,
            row != null, row?.UpdatedAt, by,
            new BillingSettingsDefaults(d.TdsCommissionRate, d.TdsCommissionThreshold, d.TdsProfessionalRate, d.TdsProfessionalThreshold,
                d.TdsNoPanRate, d.FrontDeskDiscountLimitPercent));
    }

    private static void Rate(decimal? v, string what)
    {
        if (v is < 0 or > 50) throw new ValidationException($"{what} must be between 0% and 50%.");
    }

    private static void Amount(decimal? v, string what)
    {
        if (v is < 0 or > 100_000_000) throw new ValidationException($"{what} must be between ₹0 and ₹10 crore.");
    }
}
