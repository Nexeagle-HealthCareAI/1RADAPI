using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

public record ReferrerIntelligenceDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    int TotalPatients,
    List<ReferredPatientDto> Patients
);

public record ReferredPatientDto(
    Guid PatientId,
    string Name,
    string Age,
    string Gender,
    string Modality,
    string RegistrationDate,
    string Status
);
