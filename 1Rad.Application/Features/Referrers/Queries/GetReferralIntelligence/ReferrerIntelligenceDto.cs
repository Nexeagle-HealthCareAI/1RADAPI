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
    decimal DiscountAmount = 0
);
