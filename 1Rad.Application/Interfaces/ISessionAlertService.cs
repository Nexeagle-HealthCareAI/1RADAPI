namespace _1Rad.Application.Interfaces;

// Heads-up notification when a new device signs in or an existing session is
// forcibly logged out by a fresh login of the same category. Intended as a
// phishing tripwire and as a courtesy message ("Were you the one who just
// signed in on a tablet?").
public interface ISessionAlertService
{
    // Fire-and-forget — the login flow must NOT block on transport latency.
    Task NotifyNewSessionAsync(NewSessionAlert alert);
}

public record NewSessionAlert(
    Guid UserId,
    string? UserEmail,
    string? UserMobile,
    string UserFullName,
    string DeviceCategory,
    string? DeviceName,
    string? IpAddress,
    DateTime LoggedInAtUtc,
    // True when this login knocked an existing session OUT; false when it's
    // simply a new device category being added to the user's active set.
    // Drives the message wording on the patient/staff side.
    bool ReplacedExistingSession
);
