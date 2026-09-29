using MediatR;
using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Hospitals.Queries.GetAllHospitalsForAdmin;

public record AdminStaffDto(
    Guid StaffId,
    string FullName,
    string? Email,
    string? Mobile,
    string? Designation,
    string? Specialization,
    string Status
);

public record AdminHospitalDto(
    Guid HospitalId,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? NABHNumber,
    string Status,
    DateTime CreatedAt,
    string SubscriptionStatus,
    string BillingCycle,
    string Modules,
    List<AdminStaffDto> Staff
);

// Platform-wide, cross-tenant listing for CMS's admin console -- see GetAllHospitalsForAdminQueryHandler.
public record GetAllHospitalsForAdminQuery() : IRequest<List<AdminHospitalDto>>;
