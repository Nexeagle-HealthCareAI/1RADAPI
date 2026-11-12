namespace _1Rad.Domain.Entities;

/// <summary>
/// A centre that follows another centre's setup (migration 120). While a part is
/// linked, saving it at the main centre saves it here too. The centre can still
/// change its own copy; the owner board then shows that part as "differs".
/// Not IHospitalContext on purpose: the owner board reads it across centres.
/// </summary>
public class HospitalSettingsLink
{
    /// <summary>The centre that follows.</summary>
    public Guid HospitalId { get; set; }
    /// <summary>The main centre it follows.</summary>
    public Guid SourceHospitalId { get; set; }
    /// <summary>Discounts, partner payouts, GST, credit notes and TDS.</summary>
    public bool LinkBilling { get; set; } = true;
    /// <summary>The price list (tests, prices, partner cut, GST rate and SAC).</summary>
    public bool LinkPrices { get; set; } = true;
    public Guid? LinkedByUserId { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}
