using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Auth.Queries.GetAuthorizedHospitals;

public class AuthorizedHospitalDto
{
    public Guid HospitalId { get; set; }
    public string HospitalName { get; set; } = string.Empty;
    public List<string> RoleNames { get; set; } = new();
    public bool IsDefault { get; set; }
}

public class GetAuthorizedHospitalsResponse
{
    public bool Success { get; set; }
    public List<AuthorizedHospitalDto> Hospitals { get; set; } = new();
    public string? Error { get; set; }
}
