using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

// Fan-out notifier — dispatches the alert via every channel we have a
// transport for (email + WhatsApp), swallowing transport failures so a
// flaky SMS gateway can't block a login.
public class SessionAlertService : ISessionAlertService
{
    private readonly IEmailService _email;
    private readonly ISmsService _sms;
    private readonly ILogger<SessionAlertService> _logger;

    public SessionAlertService(IEmailService email, ISmsService sms, ILogger<SessionAlertService> logger)
    {
        _email = email;
        _sms = sms;
        _logger = logger;
    }

    public async Task NotifyNewSessionAsync(NewSessionAlert alert)
    {
        // We compose ONE short human-readable message and reuse it across
        // channels. Wording handles both "first time we've seen this device"
        // and "your previous session got kicked".
        var when = alert.LoggedInAtUtc.ToLocalTime().ToString("HH:mm dd MMM yyyy");
        var device = string.IsNullOrWhiteSpace(alert.DeviceName)
            ? alert.DeviceCategory.ToLowerInvariant() + " device"
            : $"{alert.DeviceName} ({alert.DeviceCategory.ToLowerInvariant()})";
        var ip = string.IsNullOrWhiteSpace(alert.IpAddress) ? "an unrecognised network" : alert.IpAddress;

        var body = alert.ReplacedExistingSession
            ? $"Hi {alert.UserFullName}, a new sign-in on {device} from {ip} at {when} replaced your previous {alert.DeviceCategory.ToLowerInvariant()} session. If this wasn't you, change your password and contact your administrator immediately."
            : $"Hi {alert.UserFullName}, new sign-in on {device} from {ip} at {when}. If this wasn't you, change your password and sign out from Settings → Active Sessions.";

        // Email — non-fatal.
        try
        {
            if (!string.IsNullOrWhiteSpace(alert.UserEmail))
            {
                await _email.SendEmailAsync(
                    alert.UserEmail,
                    "1Rad sign-in alert",
                    body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session alert email failed for user {UserId}", alert.UserId);
        }

        // WhatsApp — also non-fatal. We currently only have a single OTP-shaped
        // template wired up; reusing it as a free-form note isn't ideal, so
        // for Phase 1 we log+skip. When a dedicated "alert" template is
        // approved by Meta this is the one line that changes.
        try
        {
            if (!string.IsNullOrWhiteSpace(alert.UserMobile))
            {
                _logger.LogInformation(
                    "Session alert (WhatsApp pending template approval) for {Mobile}: {Body}",
                    alert.UserMobile, body);
                // Intentionally not calling _sms here yet.
                _ = _sms;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session alert WhatsApp failed for user {UserId}", alert.UserId);
        }
    }
}
