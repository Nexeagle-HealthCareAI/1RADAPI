namespace _1Rad.Application.Interfaces;

// Mints + validates short-lived signed tokens for the patient-facing /track
// QR. The QR URL stays unguessable on its own (the appointment Guid alone is
// 128 bits of entropy), but adding a signed token lets us:
//   • Set an explicit expiry — printed slips stop working after the patient's
//     "report shelf life" window, so a leaked QR doesn't grant access forever.
//   • Revoke en masse by rotating the signing key, e.g. after a breach.
//   • Bind a token to a single appointment so re-pointing at a different
//     appointment id is detected as tamper.
public interface ITrackingTokenService
{
    // Mint a token for the given appointment. The default 1-year lifetime
    // covers normal patient re-viewing of their report; callers can pass a
    // shorter ttl for sensitive surfaces.
    string Issue(Guid appointmentId, TimeSpan? ttl = null);

    // Verify a token and its signature. Returns true only if signed by the
    // current secret AND not expired AND the embedded appointmentId matches
    // the one supplied. Constant-time comparison protects against signature
    // probing.
    bool Validate(string token, Guid expectedAppointmentId);
}
