using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

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
    decimal TotalRevenue = 0,
    decimal TotalDiscount = 0,
    decimal NetProfit = 0
);

public record ReferredPatientDto(
    Guid PatientId,
    string PatientIdentifier,
    string Name,
    string Mobile,
    string Address,
    string Age,
    string Gender,
    string Modality,
    string Service,
    string SourceOfInfo,
    string RegistrationDate,
    string Status,
    Guid? AppointmentId = null,
    decimal CommissionAmount = 0,
    string CommissionStatus = "Unpaid",
    decimal TotalAmount = 0,
    string? ReferrerName = null,
    decimal DiscountAmount = 0,
    // Multi-service rollout (batch-5 fix). The Modality + Service fields
    // above still carry the parent's primary scalar for backward
    // compatibility, so existing list / row renderers don't break.
    // ServiceLines is the per-line breakdown — used by ReferralsPage's
    // chart aggregators so a multi-service visit contributes to the
    // CT bucket AND the USG bucket, not just the X-Ray primary.
    IReadOnlyList<ReferredServiceLineDto>? ServiceLines = null
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
