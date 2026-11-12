using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A centre's own tax and billing switches, set by its admins (migration 109).
/// No row means every default applies: GST worked out from the price list,
/// credit notes issued, TDS deducted on referral payouts at the rates in the
/// server's settings, front desk limited to 10% off. A null rate or limit means
/// "use the default".
/// </summary>
public class HospitalBillingSettings : IHospitalContext
{
    public Guid HospitalId { get; set; }

    /// <summary>Off: every bill line is charged 0% GST, bills print as plain bills and no GSTR-1 sheet is offered.</summary>
    public bool GstEnabled { get; set; } = true;

    /// <summary>Off: no credit notes or refund vouchers are raised (refunds still go through the payment ledger).</summary>
    public bool CreditNotesEnabled { get; set; } = true;

    /// <summary>Off: referral payouts are made without deducting TDS.</summary>
    public bool TdsEnabled { get; set; } = true;
    public string DefaultTdsCategory { get; set; } = "COMMISSION";
    public decimal? TdsCommissionRate { get; set; }
    public decimal? TdsCommissionThreshold { get; set; }
    public decimal? TdsProfessionalRate { get; set; }
    public decimal? TdsProfessionalThreshold { get; set; }
    public decimal? TdsNoPanRate { get; set; }

    /// <summary>Most the front desk may take off a bill (centre discount + deduction), as % of the bill.</summary>
    public decimal? FrontDeskDiscountLimitPercent { get; set; }

    public Guid? UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
