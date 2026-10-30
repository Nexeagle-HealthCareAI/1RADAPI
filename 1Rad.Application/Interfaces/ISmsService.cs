namespace _1Rad.Application.Interfaces;

public interface ISmsService
{
    Task SendOtpAsync(string mobile, string otp);

    // Sends a referring doctor their private portal link over WhatsApp via the
    // NexEagle WhatsApp Business API, using an approved utility template. The
    // template ("referral_portal" by default) takes three body parameters in
    // order: doctor name, centre name, and the full dashboard link. Throws on a
    // gateway failure (e.g. template not approved) so the caller can report it.
    Task SendReferralLinkAsync(string mobile, string doctorName, string centreName, string link);

    // Tells a patient their report is ready (template Notifications:ReportReady:TemplateName,
    // default "report_ready"). Body parameters in order: patient name, centre name, link.
    // Throws on a gateway failure; callers treat it as best-effort.
    Task SendReportReadyAsync(string mobile, string patientName, string centreName, string link);
}
