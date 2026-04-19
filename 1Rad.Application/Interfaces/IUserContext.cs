namespace _1Rad.Application.Interfaces;

public interface IUserContext
{
    Guid UserId { get; }
    Guid HospitalId { get; }
    IEnumerable<int> RoleIds { get; }
    Guid? GroupId { get; }
    IEnumerable<Guid> AuthorizedHospitalIds { get; }
}
