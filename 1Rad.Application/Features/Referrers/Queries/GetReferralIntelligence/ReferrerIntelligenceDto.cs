using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

public record ReferrerIntelligenceDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    int TotalPatients,
    List<ReferredPatientDto> Patients
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
    string RegistrationDate,
    string Status
);
