using MediatR;

namespace _1Rad.Application.Features.Staff.Queries.GetStaffDocuments;

public record GetStaffDocumentsQuery(Guid StaffId, Guid HospitalId) : IRequest<List<StaffDocumentDto>>;
