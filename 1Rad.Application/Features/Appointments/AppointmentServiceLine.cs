namespace _1Rad.Application.Features.Appointments;

/// <summary>
/// One line item in a multi-service appointment payload.
///
/// Embedded inside CreateAppointmentCommand / UpdateAppointmentCommand
/// so a single visit can carry many services (X-ray + CT + USG together).
/// When the legacy scalar Service/Modality/Amount/ReferralCutValue fields
/// are populated on a command and Services is null/empty, the handlers
/// synthesise a single AppointmentServiceLine from the scalars so v1
/// clients (older PWA installs, the offline outbox) keep working
/// unchanged through the rollout.
///
/// <see cref="Id"/> is optional and only meaningful on Update — it lets
/// the reconciler keep existing AppointmentService rows in place
/// (preserving their status, TAT timestamps, FK pointers on reports /
/// studies / commissions) rather than treating every save as a wipe-
/// and-recreate.
/// </summary>
public record AppointmentServiceLine(
    string ServiceName,
    string Modality,
    decimal Amount = 0,
    decimal ReferralCutValue = 0,
    Guid? Id = null,
    Guid? ServiceChargeId = null
);
