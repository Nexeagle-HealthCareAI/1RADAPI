using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

/// <summary>
/// One referral SOURCE. <see cref="SourceKind"/> says what it is:
///   PARTNER      - a real referral partner (ReferrerId is its primary partner id)
///   SELF         - walk-in / self (ReferrerId is empty; a bucket, not a partner)
///   UNLINKED     - the visit names a referrer with no partner record (ReferrerId empty, Name = the typed name)
///   UNATTRIBUTED - the visit records no referrer at all (ReferrerId empty)
/// Clients must use SourceKind, NOT "ReferrerId is empty", to tell Self from the others.
///
/// Visits: TotalPatients / Patients are ATTENDED visits only (the patient arrived). Booked-not-arrived
/// and no-show visits are counted separately in BookedPending / NoShows.
/// </summary>
public record ReferrerIntelligenceDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    int TotalPatients,
    List<ReferredPatientDto> Patients,
    decimal TotalCommission = 0,
    decimal PaidCommission = 0,
    decimal UnpaidCommission = 0,
    // Billed: the invoice total (after discount) of the attended visits. Not cash received.
    decimal TotalRevenue = 0,
    decimal TotalDiscount = 0,
    // Billed minus commission. NOT profit - no clinic costs are deducted.
    decimal NetProfit = 0,
    string SourceKind = "PARTNER",
    // Booked and still ahead (today or later) / booked in the past and never arrived.
    int BookedPending = 0,
    int NoShows = 0,
    // Distinct people among the attended visits; visits that were the patient's FIRST EVER
    // attended visit at the centre (new patients this source brought); the rest are repeat visits.
    int UniquePatients = 0,
    int NewPatients = 0,
    int ReturningVisits = 0,
    // Cash actually collected against the attended visits' invoices.
    decimal TotalCollected = 0,
    // Stable id of this source for the visits drill-down (a partner's id, "self", "unattributed"
    // or "name:XYZ"). Unlike ReferrerId it is set for every kind of source.
    string SourceKey = "",
    // Service lines per modality across ALL the source's attended visits (a CT + USG visit counts
    // once in each), so a summary row can draw the modality mix without any visit rows.
    Dictionary<string, int>? Modalities = null,
    // Commission still owed to this source, by scan type. Adds up to UnpaidCommission (same rows).
    Dictionary<string, decimal>? UnpaidByModality = null
);

public record ReferredPatientDto(
    Guid PatientId,
    // Nullable because the columns are: a patient with no mobile / age / gender, or a visit with no
    // modality or service, really does come back as JSON null (and a summary never reads them at all).
    string? PatientIdentifier,
    string? Name,
    string? Mobile,
    string? Address,
    string? Age,
    string? Gender,
    string? Modality,
    string? Service,
    string SourceOfInfo,
    // The visit's date in IST (yyyy-MM-dd). It used to be the UTC date, which put a
    // visit after 18:30 IST on the wrong day.
    string RegistrationDate,
    string? Status,
    Guid? AppointmentId = null,
    decimal CommissionAmount = 0,
    string CommissionStatus = "Unpaid",
    decimal TotalAmount = 0,
    string? ReferrerName = null,
    decimal DiscountAmount = 0,
    // Multi-service rollout (batch-5 fix). The Modality + Service fields
    // above still carry the parent's primary scalar for backward
    // compatibility, so existing list / row renderers don't break.
    // ServiceLines is the per-line breakdown - used by ReferralsPage's
    // chart aggregators so a multi-service visit contributes to the
    // CT bucket AND the USG bucket, not just the X-Ray primary.
    IReadOnlyList<ReferredServiceLineDto>? ServiceLines = null,
    // The part of CommissionAmount that has NOT been paid yet. CommissionStatus is
    // a single any-unpaid flag for the visit, so a visit with one paid and one
    // unpaid service line read as fully "Unpaid" - summing CommissionAmount by
    // that flag overstated outstanding liability. Sum this instead.
    decimal UnpaidAmount = 0,
    // The visit's date AND time in IST ("yyyy-MM-ddTHH:mm") - lets clients bucket by hour
    // (the date-only RegistrationDate put every visit in "Morning").
    string VisitAt = "",
    // True when this was the patient's first attended visit at the centre (any source).
    bool IsFirstVisit = false,
    // Cash collected against this visit's invoice.
    decimal PaidAmount = 0
);

/// <summary>
/// One line of work on a referred appointment, surfaced through the
/// referrer dashboard so per-modality breakdowns stay accurate when a
/// visit carries many services.
/// </summary>
public record ReferredServiceLineDto(
    Guid AppointmentServiceId,
    string ServiceName,
    string Modality,
    decimal CommissionAmount
);
