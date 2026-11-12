namespace _1Rad.Application.Interfaces;

/// <summary>
/// Lets one request read or act on a centre other than the one in the login
/// token, for the owner board only. While a scope from <see cref="Act"/> is open,
/// <see cref="IUserContext.HospitalId"/> (and so every per-centre query filter)
/// points at that centre. Callers must have checked that the user is an admin of
/// the centre first (see Features/Owner/OwnerAccess).
/// </summary>
public interface IActingCentre
{
    Guid? HospitalId { get; }
    IDisposable Act(Guid hospitalId);
}
