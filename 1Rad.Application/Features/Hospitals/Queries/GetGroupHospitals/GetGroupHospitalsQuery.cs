using MediatR;
using System;
using System.Collections.Generic;
using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;

namespace _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;

public record GetGroupHospitalsQuery() : IRequest<List<HospitalDetailsDto>>;
