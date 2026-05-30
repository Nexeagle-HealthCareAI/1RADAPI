namespace _1Rad.Application.Interfaces;

public interface IUserContext
{
    Guid UserId { get; }
    Guid HospitalId { get; }
    IEnumerable<int> RoleIds { get; }
    Guid? GroupId { get; }
    IEnumerable<Guid> AuthorizedHospitalIds { get; }

    // Stable per-session identifier. Null for legacy tokens minted before
    // migration 46. Carried forward through SwitchContext + refresh so the
    // user's continuous presence on a single device is a single session.
    Guid? SessionId { get; }
}
